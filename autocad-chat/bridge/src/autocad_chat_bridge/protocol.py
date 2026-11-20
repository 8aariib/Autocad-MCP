"""The line protocol between the AutoCAD panel and this bridge.

One JSON object per line, UTF-8, on stdin (panel -> bridge) and stdout
(bridge -> panel). Nothing else is ever written to stdout: logs go to stderr.

Panel -> bridge
    {"type": "send", "text": str, "context": {...}}   start a turn
    {"type": "cancel"}                                stop the running turn
    {"type": "reset"}                                 forget the conversation
    {"type": "confirm", "id": str, "approved": bool}  answer a confirm_request
    {"type": "settings", "model"?, "effort"?, "confirm_destructive"?}
    {"type": "shutdown"}

Bridge -> panel
    {"type": "status", "text"}                        short state line
    {"type": "ready", "model", "tools"}               tools are connected
    {"type": "turn_start"}
    {"type": "text_delta", "text"}                    streamed answer text
    {"type": "progress", "text"}                      Claude's note between tool calls
    {"type": "tool_start", "id", "name", "label"}
    {"type": "tool_end", "id", "ok", "summary", "image"?}   image: base64 PNG
    {"type": "confirm_request", "id", "tool", "detail"}
    {"type": "turn_end", "stop_reason", "usage"}
    {"type": "error", "message", "fatal"}
"""

from __future__ import annotations

import asyncio
import json
import sys
import threading
from typing import Any, Callable

PROTOCOL_VERSION = 1


class Channel:
    """Reads panel messages from a text stream and writes events to another."""

    def __init__(self, reader=None, writer=None) -> None:
        self._reader = reader if reader is not None else sys.stdin
        self._writer = writer if writer is not None else sys.stdout
        self._lock = threading.Lock()

    def emit(self, type_: str, **fields: Any) -> None:
        line = json.dumps({"type": type_, **fields}, ensure_ascii=False, separators=(",", ":"))
        with self._lock:
            self._writer.write(line + "\n")
            self._writer.flush()

    def start_reader(self, loop: asyncio.AbstractEventLoop, on_message: Callable[[dict], None]) -> None:
        """Read lines on a daemon thread; hand each parsed object to the loop.

        A thread, not asyncio streams, because the Windows proactor loop cannot
        read a pipe handed over as stdin. EOF becomes a shutdown message.
        """

        def pump() -> None:
            for raw in self._reader:
                raw = raw.strip()
                if not raw:
                    continue
                try:
                    msg = json.loads(raw)
                except json.JSONDecodeError:
                    loop.call_soon_threadsafe(on_message, {"type": "_bad_line", "line": raw[:200]})
                    continue
                if isinstance(msg, dict):
                    loop.call_soon_threadsafe(on_message, msg)
            loop.call_soon_threadsafe(on_message, {"type": "shutdown"})

        threading.Thread(target=pump, name="panel-reader", daemon=True).start()
