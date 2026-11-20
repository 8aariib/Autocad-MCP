"""The no-API-key engine: Claude Code, signed in with the user's Claude account.

Claude Code runs headless (`claude -p`, stream-json in and out) with every
built-in tool removed, so the only tools it has are the AutoCAD MCP Pro ones.
Each AutoCAD tool call arrives here as a `can_use_tool` permission request;
safe tools are allowed at once and destructive ones go to the panel first.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import shutil
import sys
import tempfile
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Awaitable, Callable

from . import guide
from .agent import Settings, tool_label
from .cad_tools import ServerOptions, server_command, summarize
from .prompts import SYSTEM_PROMPT, context_block

log = logging.getLogger("autocad_chat_bridge.claude_code")

MCP_NAME = "autocad"
PREFIX = f"mcp__{MCP_NAME}__"
INTERRUPT_GRACE_S = 15


def find_claude() -> list[str] | None:
    """The command that runs Claude Code, or None. A .cmd shim (npm install) runs through cmd.exe."""
    override = os.environ.get("ACADCHAT_CLAUDE_CMD")
    if override:
        return json.loads(override)
    candidates = []
    if sys.platform == "win32":
        home = Path.home()
        candidates += [home / ".local" / "bin" / "claude.exe", Path(os.environ.get("APPDATA", "")) / "npm" / "claude.cmd"]
    found = shutil.which("claude")
    if found:
        candidates.append(Path(found))
    for c in candidates:
        if c and c.exists():
            if c.suffix.lower() in (".cmd", ".bat"):
                return [os.environ.get("COMSPEC", "cmd.exe"), "/c", str(c)]
            return [str(c)]
    return None


def system_prompt_text() -> str:
    playbook = guide.playbook().replace("](read_guide:", "](reference page, not available in this panel: ")
    return (
        SYSTEM_PROMPT.replace("cad_batch, drawing_info and entity_list are listed directly.",
                              "cad_batch, drawing_info and entity_list are listed directly. Tool names carry the prefix mcp__autocad__.")
        + "\n\n<drafting_playbook>\nFor work whose result someone will rely on (a part drawn from a dimensioned source, "
        "a delivery, a takeoff), follow this playbook. For a quick edit, reading back what you changed is enough.\n\n"
        + playbook
        + "\n</drafting_playbook>"
    )


def mcp_config(options: ServerOptions) -> dict:
    full = options.env()
    keep = {k: full[k] for k in full if k in (
        "AUTOCAD_MCP_BACKEND", "CAD_LAUNCH", "DISCOVERY_MODE", "DANGEROUS_COMMANDS_ENABLED", "MAX_TOOL_COST",
        "READ_ONLY", "LOG_LEVEL", "PYTHONIOENCODING", "ALLOWED_PATHS") or k in options.extra_env}
    cmd = server_command()
    return {"mcpServers": {MCP_NAME: {"type": "stdio", "command": cmd[0], "args": cmd[1:], "env": keep}}}


Emit = Callable[..., None]
Confirm = Callable[[str, str], Awaitable[bool]]


@dataclass
class ClaudeCodeConversation:
    """Same surface as agent.Conversation: run_turn, reset, settings."""

    claude_cmd: list[str]
    server_options: ServerOptions
    costs: Any  # CadTools on the headless engine: answers destructive_targets() only
    emit: Emit
    confirm: Confirm
    settings: Settings = field(default_factory=Settings)
    workdir: Path = field(default_factory=lambda: Path(tempfile.mkdtemp(prefix="acadchat-")))

    _proc: asyncio.subprocess.Process | None = None
    _reader: asyncio.Task | None = None
    _events: asyncio.Queue | None = None
    _doc: str | None = None
    _ids: dict = field(default_factory=dict)  # tool_use_id -> panel row id
    _n: int = 0
    tool_count: int = 0

    # -- process ----------------------------------------------------------------

    def _argv(self) -> list[str]:
        (self.workdir / "system-prompt.md").write_text(system_prompt_text(), encoding="utf-8")
        (self.workdir / "mcp.json").write_text(json.dumps(mcp_config(self.server_options)), encoding="utf-8")
        return [
            *self.claude_cmd, "-p",
            "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--include-partial-messages",
            "--restricted", "--tools", "",
            "--strict-mcp-config", "--mcp-config", str(self.workdir / "mcp.json"),
            "--permission-mode", "manual", "--permission-prompts", "host", "--permission-prompt-tool", "stdio",
            "--no-session-persistence",
            "--model", self.settings.model, "--effort", self.settings.effort,
            "--system-prompt-file", str(self.workdir / "system-prompt.md"),
        ]

    async def start(self) -> None:
        env = dict(os.environ)
        # Use the Claude account login, never a stray key from the environment.
        for k in ("ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN"):
            env.pop(k, None)
        self._proc = await asyncio.create_subprocess_exec(
            *self._argv(), cwd=str(self.workdir), env=env,
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
            limit=64 * 1024 * 1024,
        )
        self._events = asyncio.Queue()
        self._reader = asyncio.create_task(self._read())
        asyncio.create_task(self._drain_stderr(self._proc))
        log.info("claude code started (pid %s)", self._proc.pid)

    async def stop(self) -> None:
        p, self._proc = self._proc, None
        if self._reader:
            self._reader.cancel()
        if p and p.returncode is None:
            try:
                p.stdin.close()
                await asyncio.wait_for(p.wait(), 3)
            except (asyncio.TimeoutError, Exception):
                p.kill()

    async def reset(self) -> None:
        await self.stop()
        self._doc = None
        await self.start()

    async def _send(self, obj: dict) -> None:
        if not self._proc or self._proc.returncode is not None:
            raise RuntimeError("Claude Code is not running")
        self._proc.stdin.write((json.dumps(obj) + "\n").encode("utf-8"))
        await self._proc.stdin.drain()

    async def _read(self) -> None:
        assert self._proc and self._events
        async for raw in self._proc.stdout:
            line = raw.decode("utf-8", "replace").strip()
            if not line:
                continue
            try:
                ev = json.loads(line)
            except json.JSONDecodeError:
                log.info("claude (non-JSON): %s", line[:300])
                continue
            if ev.get("type") == "control_request":
                # Answer permission requests without waiting for the turn loop.
                asyncio.create_task(self._on_control(ev))
            elif ev.get("type") == "system" and ev.get("subtype") == "init":
                self.tool_count = len([t for t in ev.get("tools", []) if t.startswith(PREFIX)])
            else:
                await self._events.put(ev)
        await self._events.put({"type": "_exit"})

    async def _drain_stderr(self, p) -> None:
        async for raw in p.stderr:
            log.info("claude: %s", raw.decode("utf-8", "replace").rstrip())

    # -- permissions ------------------------------------------------------------

    async def _on_control(self, ev: dict) -> None:
        req = ev.get("request") or {}
        rid = ev.get("request_id")
        if req.get("subtype") != "can_use_tool":
            await self._send({"type": "control_response", "response": {"subtype": "error", "request_id": rid, "error": "unsupported"}})
            return
        name = str(req.get("tool_name") or "")
        args = req.get("input") or {}
        allow = name.startswith(PREFIX)
        message = "Only the AutoCAD tools are available in this panel."
        if allow and self.settings.confirm_destructive:
            short = name[len(PREFIX):]
            risky = await self.costs.destructive_targets(short, args)
            if risky:
                row = self._row(req.get("tool_use_id"))
                allow = await self.confirm(row, f"{tool_label(short, args)}\n\nThis runs: {', '.join(risky)}")
                message = "The user declined this change in the panel. Do not retry it; ask what they want instead."
        response = {"behavior": "allow", "updatedInput": args} if allow else {"behavior": "deny", "message": message}
        await self._send({"type": "control_response", "response": {"subtype": "success", "request_id": rid, "response": response}})

    def _row(self, tool_use_id: str | None) -> str:
        key = tool_use_id or uuid.uuid4().hex
        if key not in self._ids:
            self._n += 1
            self._ids[key] = f"t{self._n}"
        return self._ids[key]

    # -- turns ------------------------------------------------------------------

    async def run_turn(self, text: str, context: dict | None = None) -> None:
        if not self._proc or self._proc.returncode is not None:
            await self.start()
        ctx = context or {}
        note = ""
        target = ctx.get("doc_path") or ctx.get("doc_name")
        if target and self._doc and target != self._doc:
            note = (f"\nThe user switched drawings since the last message. Before anything else, call document_activate "
                    f"with name_or_path={target!r}.")
        if target:
            self._doc = target
        content = [{"type": "text", "text": context_block(ctx) + note}, {"type": "text", "text": text}]
        self.emit("turn_start")
        try:
            await self._send({"type": "user", "message": {"role": "user", "content": content}})
            await self._pump_turn()
        except asyncio.CancelledError:
            await self._interrupt()
            self.emit("turn_end", stop_reason="cancelled", usage={})
            raise

    async def _pump_turn(self) -> None:
        assert self._events
        while True:
            ev = await self._events.get()
            kind = ev.get("type")
            if kind == "_exit":
                self.emit("error", message="Claude Code stopped unexpectedly. Check that you are signed in (run: claude auth login).", fatal=False)
                self.emit("turn_end", stop_reason="error", usage={})
                self._proc = None
                return
            if kind == "stream_event":
                e = ev.get("event") or {}
                if e.get("type") == "content_block_delta" and (e.get("delta") or {}).get("type") == "text_delta":
                    self.emit("text_delta", text=e["delta"].get("text", ""))
            elif kind == "assistant":
                for block in (ev.get("message") or {}).get("content") or []:
                    if block.get("type") == "tool_use":
                        name = str(block.get("name", ""))
                        short = name[len(PREFIX):] if name.startswith(PREFIX) else name
                        self.emit("tool_start", id=self._row(block.get("id")), name=short, label=tool_label(short, block.get("input") or {}))
            elif kind == "user":
                content = (ev.get("message") or {}).get("content")
                for block in content if isinstance(content, list) else []:
                    if isinstance(block, dict) and block.get("type") == "tool_result":
                        self._tool_end(block)
            elif kind == "result":
                self._turn_end(ev)
                return

    def _tool_end(self, block: dict) -> None:
        raw = block.get("content")
        items = raw if isinstance(raw, list) else [{"type": "text", "text": str(raw or "")}]
        blocks, image = [], None
        for it in items:
            if not isinstance(it, dict):
                continue
            if it.get("type") == "text":
                blocks.append({"type": "text", "text": it.get("text", "")})
            elif it.get("type") == "image":
                image = image or (it.get("source") or {}).get("data")
        is_error = bool(block.get("is_error"))
        summary = summarize(blocks or [{"type": "text", "text": ""}], is_error) if blocks else ("image" if image else "")
        self.emit("tool_end", id=self._row(block.get("tool_use_id")), ok=not is_error, summary=summary, image=image)

    def _turn_end(self, ev: dict) -> None:
        u = ev.get("usage") or {}
        inp, cr, cw = u.get("input_tokens", 0) or 0, u.get("cache_read_input_tokens", 0) or 0, u.get("cache_creation_input_tokens", 0) or 0
        usage = {"input": inp, "output": u.get("output_tokens", 0) or 0, "cache_read": cr, "cache_write": cw, "context": inp + cr + cw}
        if ev.get("is_error") or ev.get("subtype") not in (None, "success"):
            text = str(ev.get("result") or ev.get("subtype") or "error")
            if "login" in text.lower() or "auth" in text.lower() or "api key" in text.lower():
                text += "\n\nSign in to Claude Code: open PowerShell and run  claude auth login"
            self.emit("error", message=text, fatal=False)
        self.emit("turn_end", stop_reason=ev.get("stop_reason") or ev.get("subtype") or "end_turn", usage=usage)

    async def _interrupt(self) -> None:
        """Ask Claude Code to stop; drain to its result so the next turn starts clean."""
        try:
            await self._send({"type": "control_request", "request_id": uuid.uuid4().hex, "request": {"subtype": "interrupt"}})
            async def drain():
                while True:
                    ev = await self._events.get()
                    if ev.get("type") in ("result", "_exit"):
                        return
            await asyncio.wait_for(drain(), INTERRUPT_GRACE_S)
        except Exception:
            log.warning("interrupt did not settle; restarting Claude Code")
            await self.stop()
