"""The AutoCAD MCP Pro server, started as a child process and spoken to over stdio."""

from __future__ import annotations

import json
import logging
import os
import shutil
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from fastmcp import Client
from fastmcp.client.transports import StdioTransport

# A tool result is cut at this many characters before it goes to Claude.
MAX_RESULT_CHARS = 60_000

# The tools the server lists in search mode that run another tool by name.
PROXY_TOOL = "call_tool"
BATCH_TOOL = "cad_batch"


@dataclass
class ServerOptions:
    """How the server is started. Every safety switch lives here."""

    allowed_paths: str = ""
    read_only: bool = False
    backend: str = "com"
    cad_launch: str = "never"
    extra_env: dict[str, str] = field(default_factory=dict)

    def env(self) -> dict[str, str]:
        env = dict(os.environ)
        # Claude never sees the key through the server, and the server never needs it.
        env.pop("ANTHROPIC_API_KEY", None)
        env.update(
            {
                "AUTOCAD_MCP_BACKEND": self.backend,
                "CAD_LAUNCH": self.cad_launch,
                "DISCOVERY_MODE": "search",
                "DANGEROUS_COMMANDS_ENABLED": "false",
                # Blocks the raw command line and AutoLISP (cost "escape").
                "MAX_TOOL_COST": "read" if self.read_only else "destructive",
                "READ_ONLY": "true" if self.read_only else "false",
                "LOG_LEVEL": "WARNING",
                "PYTHONIOENCODING": "utf-8",
            }
        )
        if self.allowed_paths:
            env["ALLOWED_PATHS"] = self.allowed_paths
        env.update(self.extra_env)
        return env


def server_command() -> list[str]:
    """The command that starts the server installed beside this bridge."""
    override = os.environ.get("ACADCHAT_SERVER_CMD")
    if override:
        return json.loads(override)
    scripts = Path(sys.executable).parent
    for name in ("autocad-mcp.exe", "autocad-mcp"):
        candidate = scripts / name
        if candidate.exists():
            return [str(candidate)]
    found = shutil.which("autocad-mcp")
    if found:
        return [found]
    raise FileNotFoundError("the autocad-mcp server is not installed beside the bridge")


@dataclass
class ToolOutcome:
    ok: bool
    blocks: list[dict]  # Claude content blocks (text and image)
    summary: str  # one short line for the panel
    image_b64: str | None = None


class CadTools:
    """One connection to the server for the life of the bridge."""

    def __init__(self, options: ServerOptions) -> None:
        cmd = server_command()
        self._transport = StdioTransport(command=cmd[0], args=cmd[1:], env=options.env())
        self._client = Client(self._transport, log_handler=_server_log)
        self._costs: dict[str, str] = {}
        self.tools: list[dict] = []
        self.instructions: str = ""

    async def __aenter__(self) -> "CadTools":
        await self._client.__aenter__()
        init = self._client.initialize_result
        self.instructions = (getattr(init, "instructions", None) or "").strip()
        self.tools = [
            {
                "name": t.name,
                "description": (t.description or "").strip() or t.name,
                "input_schema": t.inputSchema or {"type": "object", "properties": {}},
            }
            for t in await self._client.list_tools()
        ]
        return self

    async def __aexit__(self, *exc) -> None:
        await self._client.__aexit__(*exc)

    # -- costs, for the destructive-change confirmation -------------------------

    async def cost_of(self, tool: str) -> str:
        """read | safe | mutate | destructive | escape (unknown tools: mutate)."""
        if tool not in self._costs:
            try:
                res = await self._client.call_tool("describe_tool", {"name": tool}, raise_on_error=False)
                card = json.loads(_text_of(res.content) or "{}")
                self._costs[tool] = str(card.get("cost") or "mutate")
            except Exception:
                self._costs[tool] = "mutate"
        return self._costs[tool]

    async def targets(self, name: str, args: dict) -> list[str]:
        """The real tools a call will run: itself, a proxied tool, or batch steps."""
        if name == PROXY_TOOL and isinstance(args.get("name"), str):
            inner = args["name"]
            inner_args = args.get("arguments") or {}
            return await self.targets(inner, inner_args if isinstance(inner_args, dict) else {})
        if name == BATCH_TOOL and isinstance(args.get("steps"), list):
            found: list[str] = []
            for step in args["steps"]:
                if isinstance(step, dict) and isinstance(step.get("tool"), str):
                    found.extend(await self.targets(step["tool"], step.get("args") or {}))
            return found
        return [name]

    async def destructive_targets(self, name: str, args: dict) -> list[str]:
        out = []
        for target in await self.targets(name, args):
            if await self.cost_of(target) in ("destructive", "escape") and target not in out:
                out.append(target)
        return out

    # -- calls ------------------------------------------------------------------

    async def call(self, name: str, args: dict) -> ToolOutcome:
        try:
            res = await self._client.call_tool(name, args, raise_on_error=False)
        except Exception as exc:  # transport or protocol failure
            msg = f"{type(exc).__name__}: {exc}"
            return ToolOutcome(False, [{"type": "text", "text": msg}], msg[:200])
        return convert_result(res.content, bool(res.is_error))


async def _server_log(message) -> None:
    """Server notifications go to the bridge log at debug level only."""
    logging.getLogger("autocad_chat_bridge.server").debug("%s", getattr(message, "data", message))


def _text_of(content: list[Any]) -> str:
    return "\n".join(getattr(c, "text", "") for c in content if getattr(c, "type", "") == "text")


def convert_result(content: list[Any], is_error: bool) -> ToolOutcome:
    """MCP content -> Claude tool_result blocks, plus a line for the panel."""
    blocks: list[dict] = []
    image_b64 = None
    used = 0
    for item in content or []:
        kind = getattr(item, "type", "")
        if kind == "text":
            text = item.text or ""
            room = MAX_RESULT_CHARS - used
            if len(text) > room:
                text = text[: max(room, 0)] + f"\n[... cut: the result was {len(item.text):,} characters; ask for fewer fields or a smaller limit]"
            used += len(text)
            blocks.append({"type": "text", "text": text})
        elif kind == "image":
            mime = getattr(item, "mimeType", "image/png") or "image/png"
            if mime in ("image/png", "image/jpeg", "image/gif", "image/webp"):
                blocks.append({"type": "image", "source": {"type": "base64", "media_type": mime, "data": item.data}})
                image_b64 = image_b64 or item.data
        elif kind == "resource":
            res = getattr(item, "resource", None)
            text = getattr(res, "text", None)
            if text:
                blocks.append({"type": "text", "text": text[:MAX_RESULT_CHARS]})
    if not blocks:
        blocks = [{"type": "text", "text": "(no output)" if not is_error else "(the tool failed without a message)"}]
    return ToolOutcome(not is_error, blocks, summarize(blocks, is_error), image_b64)


def summarize(blocks: list[dict], is_error: bool) -> str:
    texts = [b["text"] for b in blocks if b["type"] == "text"]
    if not texts:
        return "image" if any(b["type"] == "image" for b in blocks) else ""
    raw = texts[0].strip()
    try:
        data = json.loads(raw)
    except (json.JSONDecodeError, ValueError):
        data = None
    if isinstance(data, dict):
        for key in ("message", "hint", "summary", "result"):
            if isinstance(data.get(key), str) and data[key].strip():
                return _one_line(data[key])
        if is_error and data.get("kind"):
            return f"error: {data['kind']}"
        if "handle" in data:
            return f"{data.get('type') or data.get('dxftype') or 'entity'} {data['handle']}"
        keys = ", ".join(list(data)[:6])
        return f"{{{keys}}}" if keys else "done"
    if isinstance(data, list):
        return f"{len(data)} item(s)"
    return _one_line(raw)


def _one_line(text: str, limit: int = 160) -> str:
    line = " ".join(text.split())
    return line if len(line) <= limit else line[: limit - 1] + "…"
