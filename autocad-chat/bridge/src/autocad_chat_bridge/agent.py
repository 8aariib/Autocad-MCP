"""The conversation with Claude: one streamed request per step, tools run in between."""

from __future__ import annotations

import asyncio
import itertools
import json
import logging
from dataclasses import dataclass, field
from typing import Any, Awaitable, Callable, Protocol

import anthropic

from . import guide
from .cad_tools import ToolOutcome
from .prompts import SYSTEM_PROMPT, context_block

log = logging.getLogger("autocad_chat_bridge.agent")

DEFAULT_MODEL = "claude-opus-5-5"
MODELS = ("claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5")
EFFORTS = ("low", "medium", "high", "xhigh")
MAX_STEPS = 60  # model requests per user turn
MAX_TOKENS = 64_000

FALLBACK_BETA = "server-side-fallback-2026-07-01"
UPDATES_BETA = "thinking-display-updates-2026-08-18"


@dataclass
class Settings:
    model: str = DEFAULT_MODEL
    effort: str = "medium"
    confirm_destructive: bool = True

    def update(self, msg: dict) -> None:
        if msg.get("model") in MODELS:
            self.model = msg["model"]
        if msg.get("effort") in EFFORTS:
            self.effort = msg["effort"]
        if isinstance(msg.get("confirm_destructive"), bool):
            self.confirm_destructive = msg["confirm_destructive"]


def request_options(model: str) -> dict[str, Any]:
    """Per-model request features. Haiku has no server-side fallback and no
    progress-update display, so it gets plain adaptive thinking."""
    if model.startswith("claude-haiku"):
        return {"betas": [], "thinking": {"type": "adaptive"}}
    return {
        "betas": [FALLBACK_BETA, UPDATES_BETA],
        "thinking": {"type": "adaptive", "display": "updates"},
        "fallbacks": "default",
    }


class Tools(Protocol):
    tools: list[dict]
    instructions: str

    async def call(self, name: str, args: dict) -> ToolOutcome: ...
    async def destructive_targets(self, name: str, args: dict) -> list[str]: ...


Emit = Callable[..., None]
Confirm = Callable[[str, str], Awaitable[bool]]


@dataclass
class Usage:
    input: int = 0
    output: int = 0
    cache_read: int = 0
    cache_write: int = 0
    context: int = 0  # prompt size of the last request

    def add(self, u: Any) -> None:
        if u is None:
            return
        inp = getattr(u, "input_tokens", 0) or 0
        cr = getattr(u, "cache_read_input_tokens", 0) or 0
        cw = getattr(u, "cache_creation_input_tokens", 0) or 0
        self.input += inp
        self.output += getattr(u, "output_tokens", 0) or 0
        self.cache_read += cr
        self.cache_write += cw
        self.context = inp + cr + cw

    def as_dict(self) -> dict:
        return dict(self.__dict__)


@dataclass
class Conversation:
    client: Any  # anthropic.AsyncAnthropic, or a test double with .beta.messages.stream
    tools: Tools
    emit: Emit
    confirm: Confirm
    settings: Settings = field(default_factory=Settings)
    messages: list[dict] = field(default_factory=list)
    _ids: Any = field(default_factory=lambda: itertools.count(1))
    _doc: str | None = None  # the drawing the tool server is bound to, as the panel names it

    def reset(self) -> None:
        self.messages.clear()

    async def sync_document(self, context: dict | None) -> None:
        """Follow the user to another open drawing.

        The server binds to the drawing that is active at its first call and
        refuses to touch another one (active_document_changed), so when the user
        switches drawings between messages, rebind it before Claude acts."""
        ctx = context or {}
        target = ctx.get("doc_path") or ctx.get("doc_name")
        if not target:
            return
        previous, self._doc = self._doc, target
        if previous is None or previous == target:
            return
        outcome = await self.tools.call("call_tool", {"name": "document_activate", "arguments": {"name_or_path": target}})
        name = ctx.get("doc_name") or target
        self.emit("status", text=f"Switched to {name}" if outcome.ok else f"Could not switch to {name}: {outcome.summary}")

    def _tool_defs(self) -> list[dict]:
        # Same order every request, so the cached prefix holds.
        defs = [*self.tools.tools, guide.tool_definition()]
        return [{**t, "eager_input_streaming": True} for t in defs]

    def _system(self) -> list[dict]:
        text = SYSTEM_PROMPT
        if self.tools.instructions:
            text += "\n\nNotes from the AutoCAD tool server:\n" + self.tools.instructions
        text += (
            "\n\n<drafting_playbook>\nFor work whose result someone will rely on (a part drawn from a dimensioned "
            "source, a delivery, a takeoff), follow this playbook; its linked pages are read with read_guide. For a "
            "quick edit, reading back what you changed is enough.\n\n" + guide.playbook() + "\n</drafting_playbook>"
        )
        return [{"type": "text", "text": text}]

    async def run_turn(self, text: str, context: dict | None = None) -> None:
        """One user message to the end of Claude's answer. Never raises on API
        errors: they become an error event and the history stays valid."""
        self.messages.append(
            {"role": "user", "content": [{"type": "text", "text": context_block(context)}, {"type": "text", "text": text}]}
        )
        usage = Usage()
        stop_reason = "end_turn"
        self.emit("turn_start")
        try:
            await self.sync_document(context)
            for _step in range(MAX_STEPS):
                response = await self._one_request(usage)
                if response is None:  # stream failed; error already reported
                    stop_reason = "error"
                    break
                stop_reason = response.stop_reason or "end_turn"
                self.messages.append({"role": "assistant", "content": response.content})
                tool_uses = [b for b in response.content if getattr(b, "type", "") == "tool_use"]

                if stop_reason == "pause_turn":
                    continue
                if stop_reason == "refusal":
                    self._close_dangling(tool_uses, "Claude declined this request.")
                    self.emit("error", message="Claude declined this request. Try rephrasing it.", fatal=False)
                    break
                if stop_reason == "max_tokens":
                    self._close_dangling(tool_uses, "The response was cut off before the tool input was complete.")
                    self.emit("error", message="The answer hit its length limit and was cut off.", fatal=False)
                    break
                if not tool_uses:
                    break
                results = []
                for block in tool_uses:
                    results.append(await self._run_tool(block))
                self.messages.append({"role": "user", "content": results})
            else:
                stop_reason = "step_limit"
                self.emit("error", message=f"Stopped after {MAX_STEPS} steps. Say 'continue' to go on.", fatal=False)
        except asyncio.CancelledError:
            self._repair_after_cancel()
            self.emit("turn_end", stop_reason="cancelled", usage=usage.as_dict())
            raise
        self.emit("turn_end", stop_reason=stop_reason, usage=usage.as_dict())

    async def _one_request(self, usage: Usage):
        opts = request_options(self.settings.model)
        kwargs: dict[str, Any] = dict(
            model=self.settings.model,
            max_tokens=MAX_TOKENS,
            system=self._system(),
            tools=self._tool_defs(),
            messages=self.messages,
            thinking=opts["thinking"],
            output_config={"effort": self.settings.effort},
            cache_control={"type": "ephemeral"},
        )
        if opts["betas"]:
            kwargs["betas"] = opts["betas"]
        if "fallbacks" in opts:
            kwargs["fallbacks"] = opts["fallbacks"]

        for attempt in range(3):
            try:
                async with self.client.beta.messages.stream(**kwargs) as stream:
                    async for event in stream:
                        etype = getattr(event, "type", "")
                        if etype == "text":
                            self.emit("text_delta", text=event.text)
                        elif etype == "content_block_stop":
                            block = getattr(event, "content_block", None)
                            if getattr(block, "type", "") == "thinking" and (getattr(block, "thinking", "") or "").strip():
                                self.emit("progress", text=block.thinking.strip())
                    response = await stream.get_final_message()
                usage.add(getattr(response, "usage", None))
                return response
            except ValueError:
                # Tool-input JSON the SDK could not parse at all; nothing to
                # answer, so ask for the step again (bounded).
                log.warning("unparseable tool input, retrying step (%d)", attempt + 1)
                continue
            except anthropic.AuthenticationError:
                self.emit("error", message="The Anthropic API key was refused. Open Settings and paste a valid key.", fatal=False)
                return None
            except anthropic.PermissionDeniedError as e:
                self.emit("error", message=f"This API key cannot use {self.settings.model}: {e.message}", fatal=False)
                return None
            except anthropic.NotFoundError:
                self.emit("error", message=f"The model {self.settings.model} was not found for this key. Pick another in Settings.", fatal=False)
                return None
            except anthropic.RateLimitError:
                self.emit("error", message="Rate limited by the Anthropic API. Wait a minute and try again.", fatal=False)
                return None
            except anthropic.BadRequestError as e:
                self.emit("error", message=f"The API rejected the request: {e.message}", fatal=False)
                return None
            except anthropic.APIStatusError as e:
                self.emit("error", message=f"Anthropic API error {e.status_code}. Try again in a moment.", fatal=False)
                return None
            except anthropic.APIConnectionError:
                self.emit("error", message="Could not reach the Anthropic API. Check the internet connection.", fatal=False)
                return None
        self.emit("error", message="Claude produced a malformed tool call three times in a row.", fatal=False)
        return None

    async def _run_tool(self, block: Any) -> dict:
        tid = f"t{next(self._ids)}"
        name = block.name
        args = block.input
        label = tool_label(name, args)
        self.emit("tool_start", id=tid, name=name, label=label)

        if not isinstance(args, dict):
            self.emit("tool_end", id=tid, ok=False, summary="malformed input")
            return _result(block.id, "INVALID_JSON: the tool input was not a JSON object; send it again.", True)

        if name == guide.TOOL_NAME:
            ok, text = guide.run(args)
            self.emit("tool_end", id=tid, ok=ok, summary=f"read {args.get('topic')}" if ok else "unknown topic")
            return _result(block.id, text, not ok)

        if self.settings.confirm_destructive:
            risky = await self.tools.destructive_targets(name, args)
            if risky:
                approved = await self.confirm(tid, f"{label}\n\nThis runs: {', '.join(risky)}")
                if not approved:
                    self.emit("tool_end", id=tid, ok=False, summary="declined by you")
                    return _result(block.id, "The user declined this change in the panel. Do not retry it; ask what they want instead.", True)

        outcome = await self.tools.call(name, args)
        self.emit("tool_end", id=tid, ok=outcome.ok, summary=outcome.summary, image=outcome.image_b64)
        return {"type": "tool_result", "tool_use_id": block.id, "content": outcome.blocks, "is_error": not outcome.ok}

    def _close_dangling(self, tool_uses: list, why: str) -> None:
        if tool_uses:
            self.messages.append({"role": "user", "content": [_result(b.id, why, True) for b in tool_uses]})

    def _repair_after_cancel(self) -> None:
        """Keep the history valid: every tool_use needs a tool_result next."""
        if not self.messages:
            return
        last = self.messages[-1]
        if last["role"] != "assistant":
            return
        pending = [b for b in last["content"] if getattr(b, "type", "") == "tool_use"]
        self._close_dangling(pending, "Stopped by the user before this ran.")


def _result(tool_use_id: str, text: str, is_error: bool) -> dict:
    return {"type": "tool_result", "tool_use_id": tool_use_id, "content": [{"type": "text", "text": text}], "is_error": is_error}


def tool_label(name: str, args: Any) -> str:
    """A short human label: the proxied tool's name, or a batch's step count."""
    if not isinstance(args, dict):
        return name
    if name == "call_tool" and isinstance(args.get("name"), str):
        return args["name"]
    if name == "cad_batch" and isinstance(args.get("steps"), list):
        tools = [s.get("tool") for s in args["steps"] if isinstance(s, dict) and s.get("tool")]
        head = ", ".join(dict.fromkeys(tools))
        return f"batch of {len(args['steps'])}: {head}" if head else f"batch of {len(args['steps'])}"
    if name == "search_tools" and args.get("query"):
        return f'search "{args["query"]}"'
    if name == guide.TOOL_NAME and args.get("topic"):
        return f"guide: {args['topic']}"
    if name == "describe_tool":
        target = args.get("name") or ", ".join(args.get("names") or [])
        return f"describe {target}" if target else name
    return name


def dumps(obj: Any) -> str:
    return json.dumps(obj, ensure_ascii=False, default=str)
