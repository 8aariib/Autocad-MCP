"""The AutoCAD MCP Pro drafting playbook, shipped with the bridge.

SKILL.md goes into the system prompt; its reference pages are served on demand
by the local `read_guide` tool, so they cost nothing until Claude needs one.
"""

from __future__ import annotations

from functools import lru_cache
from importlib import resources

TOOL_NAME = "read_guide"


@lru_cache(maxsize=1)
def topics() -> tuple[str, ...]:
    folder = resources.files(__package__) / "guide"
    return tuple(sorted(p.name[:-3] for p in folder.iterdir() if p.name.endswith(".md") and p.name != "SKILL.md"))


@lru_cache(maxsize=None)
def read(name: str) -> str:
    return (resources.files(__package__) / "guide" / f"{name}.md").read_text(encoding="utf-8")


def playbook() -> str:
    text = read("SKILL")
    if text.startswith("---"):  # drop the skill front matter
        text = text.split("---", 2)[2].lstrip()
    return text.replace("](references/", "](read_guide:").replace("](examples/", "](not shipped: ")


def tool_definition() -> dict:
    return {
        "name": TOOL_NAME,
        "description": "Read one reference page of the AutoCAD MCP Pro drafting playbook (the pages the playbook links to).",
        "input_schema": {
            "type": "object",
            "properties": {"topic": {"type": "string", "enum": list(topics())}},
            "required": ["topic"],
            "additionalProperties": False,
        },
    }


def run(args: dict) -> tuple[bool, str]:
    topic = args.get("topic")
    if topic not in topics():
        return False, f"Unknown topic {topic!r}. Topics: {', '.join(topics())}"
    return True, read(topic)
