"""Entry point: `autocad-chat-bridge`, started by the AutoCAD panel.

Configuration comes from the environment the panel sets:
    ANTHROPIC_API_KEY             required
    ACADCHAT_MODEL / ACADCHAT_EFFORT / ACADCHAT_CONFIRM ("1"/"0")
    ACADCHAT_ALLOWED_PATHS        folders the server may open and save in
    ACADCHAT_READ_ONLY            "1" for look-but-don't-touch
    ACADCHAT_BACKEND              "com" (default) or "ezdxf" for tests
"""

from __future__ import annotations

import asyncio
import io
import logging
import os
import sys
import traceback

import anthropic

from . import __version__
from .agent import Conversation, Settings
from .cad_tools import CadTools, ServerOptions
from .protocol import PROTOCOL_VERSION, Channel

log = logging.getLogger("autocad_chat_bridge")


def _flag(name: str, default: bool) -> bool:
    value = os.environ.get(name)
    return default if value is None else value.strip().lower() in ("1", "true", "yes", "on")


class Bridge:
    def __init__(self, channel: Channel, client=None, tools_factory=None) -> None:
        self.channel = channel
        self.settings = Settings()
        self.settings.update(
            {
                "model": os.environ.get("ACADCHAT_MODEL"),
                "effort": os.environ.get("ACADCHAT_EFFORT"),
                "confirm_destructive": _flag("ACADCHAT_CONFIRM", True),
            }
        )
        self._client = client
        self._tools_factory = tools_factory
        self._inbox: asyncio.Queue[dict] = asyncio.Queue()
        self._turn: asyncio.Task | None = None
        self._confirms: dict[str, asyncio.Future] = {}
        self.conversation: Conversation | None = None

    def emit(self, type_: str, **fields) -> None:
        self.channel.emit(type_, **fields)

    async def confirm(self, cid: str, detail: str) -> bool:
        fut = asyncio.get_running_loop().create_future()
        self._confirms[cid] = fut
        tool = detail.splitlines()[0] if detail else ""
        self.emit("confirm_request", id=cid, tool=tool, detail=detail)
        try:
            return bool(await fut)
        finally:
            self._confirms.pop(cid, None)

    def _tools(self) -> CadTools:
        if self._tools_factory:
            return self._tools_factory()
        return CadTools(
            ServerOptions(
                allowed_paths=os.environ.get("ACADCHAT_ALLOWED_PATHS", ""),
                read_only=_flag("ACADCHAT_READ_ONLY", False),
                backend=os.environ.get("ACADCHAT_BACKEND", "com"),
            )
        )

    async def run(self) -> int:
        loop = asyncio.get_running_loop()
        self.channel.start_reader(loop, self._inbox.put_nowait)
        self.emit("status", text="Starting the AutoCAD tools…")
        if self._client is None:
            if not os.environ.get("ANTHROPIC_API_KEY"):
                self.emit("error", message="No Anthropic API key is set. Open Settings and paste one.", fatal=True)
                return 2
            self._client = anthropic.AsyncAnthropic(max_retries=3)
        try:
            async with self._tools() as tools:
                self.conversation = Conversation(self._client, tools, self.emit, self.confirm, self.settings)
                self.emit("ready", model=self.settings.model, tools=len(tools.tools), version=__version__, protocol=PROTOCOL_VERSION)
                return await self._serve()
        except Exception as exc:
            log.error("bridge failed: %s", traceback.format_exc())
            self.emit("error", message=f"The AutoCAD tool server could not start: {exc}", fatal=True)
            return 1

    async def _serve(self) -> int:
        while True:
            msg = await self._inbox.get()
            kind = msg.get("type")
            if kind == "send":
                if self._turn and not self._turn.done():
                    self.emit("error", message="Claude is still working on the last message.", fatal=False)
                    continue
                text = str(msg.get("text") or "").strip()
                if not text:
                    continue
                self._turn = asyncio.create_task(self._guarded_turn(text, msg.get("context")))
            elif kind == "cancel":
                await self._cancel_turn()
            elif kind == "reset":
                await self._cancel_turn()
                self.conversation.reset()
                self.emit("status", text="New conversation.")
            elif kind == "confirm":
                fut = self._confirms.get(str(msg.get("id")))
                if fut and not fut.done():
                    fut.set_result(bool(msg.get("approved")))
            elif kind == "settings":
                self.settings.update(msg)
                self.emit("status", text=f"Model: {self.settings.model}, effort: {self.settings.effort}")
            elif kind == "shutdown":
                await self._cancel_turn()
                return 0
            elif kind == "_bad_line":
                log.warning("ignored a malformed line from the panel: %s", msg.get("line"))

    async def _guarded_turn(self, text: str, context) -> None:
        try:
            await self.conversation.run_turn(text, context)
        except asyncio.CancelledError:
            pass
        except Exception as exc:
            log.error("turn failed: %s", traceback.format_exc())
            self.emit("error", message=f"Something went wrong: {exc}", fatal=False)
            self.emit("turn_end", stop_reason="error", usage={})

    async def _cancel_turn(self) -> None:
        for fut in list(self._confirms.values()):
            if not fut.done():
                fut.set_result(False)
        if self._turn and not self._turn.done():
            self._turn.cancel()
            try:
                await self._turn
            except (asyncio.CancelledError, Exception):
                pass


def main() -> None:
    # The panel reads UTF-8 lines; never let a cp1252 console encoding win.
    stdin = io.TextIOWrapper(sys.stdin.buffer, encoding="utf-8", errors="replace")
    stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", newline="\n", write_through=True)
    logging.basicConfig(
        level=os.environ.get("ACADCHAT_LOG_LEVEL", "INFO"),
        stream=sys.stderr,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    code = asyncio.run(Bridge(Channel(stdin, stdout)).run())
    sys.exit(code)


if __name__ == "__main__":
    main()
