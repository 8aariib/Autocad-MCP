"""End to end against the real AutoCAD MCP Pro server (headless engine), with a
scripted model. The only thing faked is Claude."""

from __future__ import annotations

import asyncio
import io
import json
import os
import sys
from pathlib import Path

import pytest

from autocad_chat_bridge.__main__ import Bridge
from autocad_chat_bridge.agent import Conversation, Settings, request_options, tool_label
from autocad_chat_bridge.cad_tools import CadTools, ServerOptions, convert_result
from autocad_chat_bridge.prompts import context_block
from autocad_chat_bridge.protocol import Channel

from fakes import FakeClient, text_turn, tool_turn

CTX = {"doc_name": "plate.dwg", "doc_path": r"C:\Work\plate.dwg", "selection": ["2A", "2B"], "units": "mm"}


@pytest.fixture
def server_options(tmp_path) -> ServerOptions:
    env = {"ACADMCP_STATE_DIR": str(tmp_path / "state"), "JOURNAL_DURABLE": "off"}
    return ServerOptions(backend="ezdxf", cad_launch="auto", allowed_paths=str(tmp_path), extra_env=env)


class Recorder:
    def __init__(self) -> None:
        self.events: list[dict] = []

    def __call__(self, type_: str, **fields) -> None:
        self.events.append({"type": type_, **fields})

    def of(self, kind: str) -> list[dict]:
        return [e for e in self.events if e["type"] == kind]


def assert_history_valid(messages: list[dict]) -> None:
    """Every tool_use is answered by a tool_result in the next message."""
    for i, m in enumerate(messages):
        if m["role"] != "assistant":
            continue
        uses = [b.id for b in m["content"] if getattr(b, "type", "") == "tool_use"]
        if not uses:
            continue
        nxt = messages[i + 1]
        assert nxt["role"] == "user"
        answered = [b["tool_use_id"] for b in nxt["content"] if b.get("type") == "tool_result"]
        assert answered == uses


async def test_draws_through_the_real_server(server_options):
    turns = [
        tool_turn("call_tool", {"name": "drawing_new", "arguments": {}}, note="Opening a new drawing first."),
        tool_turn("call_tool", {"name": "entity_create_line", "arguments": {"x1": 0, "y1": 0, "x2": 100, "y2": 0}}),
        tool_turn("entity_list", {}),
        text_turn("I drew a 100 mm line on layer 0."),
    ]
    client = FakeClient(turns)
    rec = Recorder()

    async def never(*_):
        raise AssertionError("nothing here is destructive")

    async with CadTools(server_options) as tools:
        assert {t["name"] for t in tools.tools} >= {"search_tools", "call_tool", "describe_tool", "cad_batch"}
        conv = Conversation(client, tools, rec, never, Settings())
        await conv.run_turn("Draw a 100 mm line", CTX)

    ends = rec.of("tool_end")
    assert [e["ok"] for e in ends] == [True, True, True], ends
    assert [e["label"] for e in rec.of("tool_start")] == ["drawing_new", "entity_create_line", "entity_list"]
    assert rec.of("progress")[0]["text"] == "Opening a new drawing first."
    assert "".join(e["text"] for e in rec.of("text_delta")).strip() == "I drew a 100 mm line on layer 0."
    assert rec.of("turn_end")[-1]["stop_reason"] == "end_turn"
    assert rec.of("turn_end")[-1]["usage"]["output"] == 80

    # The line really exists: entity_list's result went back to Claude.
    listing = conv.messages[6]["content"][0]["content"][0]["text"]
    assert "LINE" in listing
    assert_history_valid(conv.messages)

    # The first message carries the drawing context ahead of the user's words.
    first = conv.messages[0]["content"]
    assert first[0]["text"].startswith("<autocad_context>") and "2A, 2B" in first[0]["text"]
    assert first[1]["text"] == "Draw a 100 mm line"

    req = client.requests[0]
    assert req["model"] == "claude-opus-5-5"
    assert req["fallbacks"] == "default"
    assert req["thinking"] == {"type": "adaptive", "display": "updates"}
    assert all(t["eager_input_streaming"] for t in req["tools"])
    # The tool list and system prompt are byte-identical on every step (cache prefix).
    assert all(r["tools"] == req["tools"] and r["system"] == req["system"] for r in client.requests)


async def test_destructive_tool_needs_approval(server_options):
    delete = tool_turn("call_tool", {"name": "entity_delete", "arguments": {"handle": "2A"}})
    client = FakeClient([delete, text_turn("Okay, I left it.")])
    rec = Recorder()
    asked: list[str] = []

    async def decline(cid, detail):
        asked.append(detail)
        return False

    async with CadTools(server_options) as tools:
        conv = Conversation(client, tools, rec, decline, Settings())
        await conv.run_turn("delete that", CTX)

    assert asked and "entity_delete" in asked[0]
    assert rec.of("tool_end")[0] == {"type": "tool_end", "id": "t1", "ok": False, "summary": "declined by you"}
    result = conv.messages[2]["content"][0]
    assert result["is_error"] and "declined" in result["content"][0]["text"]
    assert_history_valid(conv.messages)


async def test_batch_with_a_destructive_step_is_confirmed(server_options):
    async with CadTools(server_options) as tools:
        steps = [{"tool": "entity_create_line", "args": {}}, {"tool": "drawing_purge", "args": {}}]
        assert await tools.destructive_targets("cad_batch", {"steps": steps}) == ["drawing_purge"]
        assert await tools.destructive_targets("call_tool", {"name": "entity_create_circle"}) == []


async def test_cancel_leaves_a_valid_history(server_options):
    slow = FakeClient([tool_turn("call_tool", {"name": "drawing_new", "arguments": {}}), text_turn("never reached " * 50)], delay=0.4)
    rec = Recorder()

    async def ok(*_):
        return True

    async with CadTools(server_options) as tools:
        conv = Conversation(slow, tools, rec, ok, Settings())
        task = asyncio.create_task(conv.run_turn("go", CTX))
        while not rec.of("tool_end"):
            await asyncio.sleep(0.01)
        await asyncio.sleep(0.1)  # inside the second, streaming request
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task
    assert rec.of("turn_end")[-1]["stop_reason"] == "cancelled"
    assert_history_valid(conv.messages)


async def test_bridge_protocol_over_lines(server_options, monkeypatch):
    """The panel's view: JSON lines in, JSON lines out."""
    client = FakeClient([tool_turn("drawing_info", {}), text_turn("Done.")])
    lines = [
        json.dumps({"type": "settings", "effort": "high"}),
        json.dumps({"type": "send", "text": "what is open?", "context": CTX}),
    ]

    class SlowReader:
        """Holds the last line back until the turn is over, like a person would."""

        def __iter__(self):
            yield from lines
            import time

            for _ in range(200):
                if '"turn_end"' in out.getvalue():
                    break
                time.sleep(0.05)
            yield json.dumps({"type": "shutdown"})

    out = io.StringIO()
    bridge = Bridge(Channel(SlowReader(), out), client=client, tools_factory=lambda: CadTools(server_options))
    code = await bridge.run()
    events = [json.loads(l) for l in out.getvalue().splitlines()]
    kinds = [e["type"] for e in events]
    assert code == 0
    assert kinds[0] == "status" and "ready" in kinds and kinds.index("ready") < kinds.index("turn_start")
    assert kinds[-1] == "turn_end"
    assert client.requests[0]["output_config"] == {"effort": "high"}
    # drawing_info with no drawing open is a typed error, shown, not a crash.
    end = next(e for e in events if e["type"] == "tool_end")
    assert end["ok"] is False and "drawing" in end["summary"].lower()


def test_request_options_per_model():
    assert request_options("claude-haiku-5-5") == {"betas": [], "thinking": {"type": "adaptive"}}
    assert request_options("claude-sonnet-5-5")["fallbacks"] == "default"


def test_labels_and_context():
    assert tool_label("call_tool", {"name": "layer_merge"}) == "layer_merge"
    assert tool_label("cad_batch", {"steps": [{"tool": "a"}, {"tool": "a"}, {"tool": "b"}]}) == "batch of 3: a, b"
    assert "Selected: nothing" in context_block({})
    big = context_block({"doc_name": "x", "selection": [str(i) for i in range(300)], "selection_count": 300})
    assert "first 200 shown" in big


def test_huge_results_are_cut():
    from types import SimpleNamespace

    out = convert_result([SimpleNamespace(type="text", text="x" * 200_000)], False)
    assert len(out.blocks[0]["text"]) < 61_000 and "cut" in out.blocks[0]["text"]


async def test_guide_tool_is_local_and_listed(server_options):
    client = FakeClient([tool_turn("read_guide", {"topic": "mechanical-editing"}), text_turn("ok")])
    rec = Recorder()

    async def never(*_):
        raise AssertionError

    async with CadTools(server_options) as tools:
        conv = Conversation(client, tools, rec, never, Settings())
        await conv.run_turn("how do I revise a part?", CTX)
    req = client.requests[0]
    assert req["tools"][-1]["name"] == "read_guide"
    assert "<drafting_playbook>" in req["system"][0]["text"] and "read_guide:" in req["system"][0]["text"]
    assert rec.of("tool_end")[0]["ok"] is True
    page = conv.messages[2]["content"][0]["content"][0]["text"]
    assert len(page) > 500


async def test_switching_drawings_rebinds_the_server():
    from autocad_chat_bridge.cad_tools import ToolOutcome

    class StubTools:
        tools: list = []
        instructions = ""

        def __init__(self):
            self.calls = []

        async def call(self, name, args):
            self.calls.append((name, args))
            return ToolOutcome(True, [{"type": "text", "text": "{}"}], "ok")

        async def destructive_targets(self, name, args):
            return []

    stub = StubTools()
    rec = Recorder()
    conv = Conversation(FakeClient([text_turn("a"), text_turn("b"), text_turn("c")]), stub, rec, None, Settings())
    await conv.run_turn("one", {"doc_name": "a.dwg", "doc_path": r"C:\\a.dwg"})
    await conv.run_turn("two", {"doc_name": "a.dwg", "doc_path": r"C:\\a.dwg"})
    assert stub.calls == []
    await conv.run_turn("three", {"doc_name": "Drawing2.dwg", "doc_path": None})
    assert stub.calls == [("call_tool", {"name": "document_activate", "arguments": {"name_or_path": "Drawing2.dwg"}})]
    assert any(e["text"] == "Switched to Drawing2.dwg" for e in rec.of("status"))
