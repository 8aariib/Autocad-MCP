"""A scripted stand-in for anthropic.AsyncAnthropic: no network, real SDK types."""

from __future__ import annotations

import asyncio
import copy
import itertools
from types import SimpleNamespace
from typing import Any

from anthropic.types.beta import BetaMessage

_ids = itertools.count(1)


def text_turn(text: str) -> dict:
    return {"content": [{"type": "text", "text": text}], "stop_reason": "end_turn"}


def tool_turn(name: str, args: dict, note: str | None = None) -> dict:
    content: list[dict] = []
    if note:
        content.append({"type": "thinking", "thinking": note, "signature": "sig"})
    content.append({"type": "tool_use", "id": f"toolu_{next(_ids):04d}", "name": name, "input": args})
    return {"content": content, "stop_reason": "tool_use"}


class _Stream:
    def __init__(self, turn: dict, delay: float) -> None:
        self._turn = turn
        self._delay = delay

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc):
        return False

    def __aiter__(self):
        return self._events()

    async def _events(self):
        for block in self._turn["content"]:
            await asyncio.sleep(self._delay)
            if block["type"] == "text":
                for word in block["text"].split(" "):
                    yield SimpleNamespace(type="text", text=word + " ")
            final = BetaMessage.model_validate(self._message()).content
            match = next(b for b in final if b.type == block["type"])
            yield SimpleNamespace(type="content_block_stop", content_block=match)

    def _message(self) -> dict:
        return {
            "id": f"msg_{next(_ids)}",
            "type": "message",
            "role": "assistant",
            "model": "claude-opus-5-5",
            "content": self._turn["content"],
            "stop_reason": self._turn["stop_reason"],
            "stop_sequence": None,
            "usage": {"input_tokens": 100, "output_tokens": 20, "cache_read_input_tokens": 50, "cache_creation_input_tokens": 0},
        }

    async def get_final_message(self) -> BetaMessage:
        return BetaMessage.model_validate(self._message())


class FakeClient:
    """Replays `turns` in order; records every request's keyword arguments."""

    def __init__(self, turns: list[dict], delay: float = 0.0) -> None:
        self.turns = list(turns)
        self.requests: list[dict[str, Any]] = []
        self.delay = delay
        self.beta = SimpleNamespace(messages=SimpleNamespace(stream=self._stream))

    def _stream(self, **kwargs):
        self.requests.append(copy.deepcopy({k: v for k, v in kwargs.items() if k != "messages"}) | {"messages": list(kwargs["messages"])})
        if not self.turns:
            raise AssertionError("the fake model ran out of scripted turns")
        return _Stream(self.turns.pop(0), self.delay)
