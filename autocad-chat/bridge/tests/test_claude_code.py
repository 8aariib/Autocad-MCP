"""The Claude-account engine, against a scripted Claude Code (no network, no account)."""

from __future__ import annotations

import asyncio
import json
import sys
from pathlib import Path

import pytest

from autocad_chat_bridge.agent import Settings
from autocad_chat_bridge.cad_tools import CadTools, ServerOptions
from autocad_chat_bridge.claude_code import ClaudeCodeConversation, find_claude, mcp_config, system_prompt_text

FAKE = [sys.executable, str(Path(__file__).with_name("fake_claude.py"))]
CTX = {"doc_name": "a.dwg", "doc_path": r"C:\a.dwg"}


@pytest.fixture
def costs_options(tmp_path):
    return ServerOptions(backend="ezdxf", cad_launch="never", extra_env={"JOURNAL_DURABLE": "off", "ACADMCP_STATE_DIR": str(tmp_path / "s")})


class Recorder(list):
    def __call__(self, type_, **fields):
        self.append({"type": type_, **fields})

    def of(self, kind):
        return [e for e in self if e["type"] == kind]


async def _conversation(costs, rec, answer: bool, tmp_path):
    async def confirm(cid, detail):
        rec("asked", id=cid, detail=detail)
        return answer

    opts = ServerOptions(backend="ezdxf", extra_env={"ACADMCP_STATE_DIR": str(tmp_path / "srv")})
    conv = ClaudeCodeConversation(FAKE, opts, costs, rec, confirm, Settings(), workdir=tmp_path)
    await conv.start()
    return conv


@pytest.mark.parametrize("answer", [True, False])
async def test_destructive_call_waits_for_the_panel(costs_options, tmp_path, answer):
    rec = Recorder()
    async with CadTools(costs_options) as costs:
        conv = await _conversation(costs, rec, answer, tmp_path)
        try:
            await conv.run_turn("delete it", CTX)
        finally:
            await conv.stop()
    asked = rec.of("asked")
    assert len(asked) == 1 and "entity_delete" in asked[0]["detail"]
    start, end = rec.of("tool_start")[0], rec.of("tool_end")[0]
    assert start["label"] == "entity_delete" and start["id"] == end["id"] == asked[0]["id"]
    assert end["ok"] is answer
    text = "".join(e["text"] for e in rec.of("text_delta")).strip()
    assert text == ("Deleted it." if answer else "Left it alone.")
    assert rec.of("turn_end")[-1]["usage"]["context"] == 113


async def test_interrupt_ends_the_turn_and_the_next_one_works(costs_options, tmp_path):
    rec = Recorder()
    async with CadTools(costs_options) as costs:
        conv = await _conversation(costs, rec, True, tmp_path)
        try:
            task = asyncio.create_task(conv.run_turn("slow please", CTX))
            while not rec.of("text_delta"):
                await asyncio.sleep(0.02)
            task.cancel()
            with pytest.raises(asyncio.CancelledError):
                await task
            assert rec.of("turn_end")[-1]["stop_reason"] == "cancelled"
            rec.clear()
            await conv.run_turn("again", CTX)
        finally:
            await conv.stop()
    assert rec.of("turn_end")[-1]["stop_reason"] == "end_turn"


async def test_switching_drawings_is_passed_on(costs_options, tmp_path):
    rec = Recorder()
    async with CadTools(costs_options) as costs:
        conv = await _conversation(costs, rec, True, tmp_path)
        try:
            await conv.run_turn("one", CTX)
            await conv.run_turn("two", {"doc_name": "b.dwg", "doc_path": r"C:\b.dwg"})
        finally:
            await conv.stop()
    # the fake echoes nothing, so check what was written for the second turn
    assert conv._doc == r"C:\b.dwg"


def test_launch_is_locked_down(tmp_path, monkeypatch):
    monkeypatch.setenv("ACADCHAT_SERVER_CMD", json.dumps(["autocad-mcp"]))
    conv = ClaudeCodeConversation(["claude"], ServerOptions(allowed_paths=r"C:\Work"), None, None, None, Settings(), workdir=tmp_path)
    argv = conv._argv()
    i = argv.index("--tools")
    assert argv[i + 1] == ""  # no built-in tools at all
    for flag in ("--restricted", "--strict-mcp-config", "--no-session-persistence"):
        assert flag in argv
    assert argv[argv.index("--permission-prompt-tool") + 1] == "stdio"
    cfg = json.loads((tmp_path / "mcp.json").read_text())["mcpServers"]["autocad"]
    assert cfg["env"]["MAX_TOOL_COST"] == "destructive" and cfg["env"]["ALLOWED_PATHS"] == r"C:\Work"
    assert "ANTHROPIC_API_KEY" not in cfg["env"]
    assert "read_guide" not in system_prompt_text()


def test_find_claude_override(monkeypatch):
    monkeypatch.setenv("ACADCHAT_CLAUDE_CMD", json.dumps(FAKE))
    assert find_claude() == FAKE
