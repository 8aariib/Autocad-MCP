"""A stand-in for `claude -p --input-format stream-json ...`, speaking the wire
format a real Claude Code run produced (recorded while building the engine).

Each user message makes it call mcp__autocad__call_tool(entity_delete), ask the
host for permission, answer with the tool result it would get, then reply.
A message containing "slow" streams slowly so an interrupt can land mid-turn.
"""

import json
import sys
import time

if sys.argv[1:3] == ["auth", "status"]:
    print(json.dumps({"loggedIn": "--logged-out" not in sys.argv}))
    sys.exit(0)


def out(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def read():
    line = sys.stdin.readline()
    return json.loads(line) if line else None


out({"type": "system", "subtype": "init", "tools": ["mcp__autocad__call_tool", "mcp__autocad__search_tools"], "session_id": "s"})
n = 0
while True:
    msg = read()
    if msg is None:
        break
    if msg.get("type") != "user":
        continue
    n += 1
    text = " ".join(b.get("text", "") for b in msg["message"]["content"])
    tid = f"toolu_{n}"
    args = {"name": "entity_delete", "arguments": {"handle": "2A"}}
    out({"type": "assistant", "message": {"role": "assistant", "content": [{"type": "tool_use", "id": tid, "name": "mcp__autocad__call_tool", "input": args}]}})
    out({"type": "control_request", "request_id": f"r{n}", "request": {"subtype": "can_use_tool", "tool_name": "mcp__autocad__call_tool", "input": args, "tool_use_id": tid}})
    resp = read()
    while resp and resp.get("type") != "control_response":
        resp = read()
    allowed = resp["response"]["response"]["behavior"] == "allow"
    result = '{"ok":true,"deleted_handle":"2A"}' if allowed else resp["response"]["response"]["message"]
    out({"type": "user", "message": {"role": "user", "content": [{"type": "tool_result", "tool_use_id": tid, "content": result, "is_error": not allowed}]}})
    reply = ("Deleted it." if allowed else "Left it alone.") + (" waiting" * 40 if "slow" in text else "")
    interrupted = False
    for word in reply.split(" "):
        out({"type": "stream_event", "event": {"type": "content_block_delta", "delta": {"type": "text_delta", "text": word + " "}}})
        if "slow" in text:
            time.sleep(0.05)
            import select

            if select.select([sys.stdin], [], [], 0)[0]:
                ctrl = read()
                if ctrl and ctrl.get("request", {}).get("subtype") == "interrupt":
                    interrupted = True
                    break
    if interrupted:
        out({"type": "result", "subtype": "error_during_execution", "is_error": True, "result": "interrupted", "usage": {}})
        continue
    out({"type": "result", "subtype": "success", "stop_reason": "end_turn", "is_error": False,
         "usage": {"input_tokens": 3, "output_tokens": 9, "cache_read_input_tokens": 100, "cache_creation_input_tokens": 10}})
