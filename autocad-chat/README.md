# Claude for AutoCAD

A chat panel docked inside AutoCAD 2025. You type; Claude reads and changes the
drawing in front of you through [AutoCAD MCP Pro](https://github.com/U-C4N/Autocad-MCP).
Claude can reach that drawing and nothing else on your computer.

## Install

1. Unzip `ClaudeForAutoCAD-1.0.0.zip` anywhere.
2. In that folder, run in PowerShell:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

   The installer adds [uv](https://docs.astral.sh/uv/) and
   [Claude Code](https://claude.com/claude-code) if they're missing, has you sign in to your
   Claude account once in the browser, copies the plugin to
   `%APPDATA%\Autodesk\ApplicationPlugins`, and prepares Python. It needs no admin rights.
3. Start AutoCAD 2025. If it asks about loading `AutoCADChat.dll`, choose **Always Load**.
   The plugin isn't code-signed, so AutoCAD asks once.
4. Type `CLAUDE` and start chatting.

### In the Claude desktop app too

The installer also adds AutoCAD as a connector in the Claude desktop app, with the same
safety settings. Quit the app fully and reopen it. In a chat, click **+**, then
**Connectors**, and turn on **autocad**. Keep AutoCAD open with a drawing. Skip this step with
`install.ps1 -NoClaudeApp`.

### Something not working?

Run `diagnose.ps1` from the same folder. It checks each piece (plugin, Python, Claude Code
and sign-in, the chat engine, the Claude app connector, and the logs). It saves
`claude-autocad-report.txt` to your Desktop and changes nothing.

### Sign-in

| Sign in with | Needs | Billed |
|---|---|---|
| **Your Claude account** (default) | Claude Code, signed in with `claude auth login` | Your Pro or Max plan's included usage |
| **An Anthropic API key** | A key from [console.anthropic.com](https://console.anthropic.com/settings/keys), pasted in Settings | Per token by Anthropic, separately from a Claude plan |

With your Claude account, the panel runs Claude Code headless with **every built-in tool
removed** (`--tools ""`, `--restricted`, `--strict-mcp-config`). Claude Code can only use the
AutoCAD tools, and every one of those calls goes to the panel for permission first.

## Using it

- `CLAUDE` opens and hides the panel. `CLAUDEOPEN` always opens it.
- Select objects first, then say "these" or "the selection". Every message tells Claude which
  drawing is active, its units, the current layer and the selected handles.
- **Enter** sends and **Shift+Enter** starts a new line. **Esc** or **Stop** stops Claude mid-answer.
- Each tool Claude runs appears as a row with ✓ or ✕ and a one-line result. Click a
  screenshot to enlarge it.
- **Ctrl+Z** in AutoCAD undoes anything Claude did.
- **New chat** clears the conversation. The footer shows how big the conversation has
  grown, so you know when to start fresh.

## Safety

| Guard | Effect |
|---|---|
| Live engine only, `CAD_LAUNCH=never` | Claude works only in the AutoCAD you opened and never starts another one |
| `MAX_TOOL_COST=destructive` | The raw command-line and AutoLISP tools are off |
| `DANGEROUS_COMMANDS_ENABLED=false` | Another block on ERASE ALL, QUIT and similar commands |
| Allowed folders (Settings) | The only folders Claude may open drawings from or save them to. The default is Documents. |
| Ask before destructive changes (on by default) | A delete, purge, merge or close waits for you to click **Allow**. This includes destructive steps inside a batch. |
| Read-only mode (Settings) | Claude can look at the drawing but not change it |
| Claude account mode | Claude Code starts with no built-in tools (no shell, no file access, no web access) and only the AutoCAD server. It keeps no session files. |
| API key (if used) | Stored encrypted with Windows DPAPI for your Windows account. Only the chat engine sees it, and the AutoCAD tool server never does. |

## How it fits together

```
AutoCAD 2025 ── AutoCADChat.dll (WPF panel) ──JSON lines──► autocad-chat-bridge (Python, via uv)
      ▲                                                        │            │
      │                                                        │ stdio MCP  │ HTTPS
      └──────────── COM ◄── autocad-mcp (AutoCAD MCP Pro) ◄────┘            ▼
                                                                    Anthropic API (Claude)
```

- **plugin/**: the AutoCAD plugin, C# on .NET 8. It holds the palette, the chat UI, settings,
  and the drawing context sent with each message. It never blocks AutoCAD's UI thread. A
  Windows job object ends the helper processes when AutoCAD exits.
- **bridge/**: the chat engine. Each step is a streamed request to Claude, and the AutoCAD
  tools run between steps. It also enforces the approval rule and ships the AutoCAD MCP Pro
  drafting playbook, which Claude reads with `read_guide`. It defaults to Claude Opus 5.5 at
  medium effort, with the server-side refusal fallback on. Sonnet 5.5 and Haiku 5.5 can be
  picked in Settings.
- **bundle/**: AutoCAD's `PackageContents.xml` and the install and uninstall scripts.

## Files on disk

| Path | What |
|---|---|
| `%APPDATA%\Autodesk\ApplicationPlugins\AutoCADChat.bundle` | The plugin |
| `%APPDATA%\AutoCADChat\settings.json` | Settings, including the encrypted key |
| `%LOCALAPPDATA%\AutoCADChat\venv` | Python environment |
| `%LOCALAPPDATA%\AutoCADChat\logs` | `panel.log` and `bridge.log` |
| `%LOCALAPPDATA%\autocad-mcp-pro\state` | The tool server's journal and checkpoints |

To uninstall, run `uninstall.ps1`. Run `uninstall.ps1 -Purge` to also remove the settings,
key, logs and Python environment.

## Troubleshooting

- **The `CLAUDE` command is unknown.** The bundle didn't load. Check that
  `%APPDATA%\Autodesk\ApplicationPlugins\AutoCADChat.bundle\PackageContents.xml` exists, then
  restart AutoCAD. `SECURELOAD` may have blocked the DLL; choose **Always Load**, or add the
  bundle folder to `TRUSTEDPATHS`.
- **"Claude Code is not signed in".** Click **Sign in** in the panel, finish in the browser, then
  click **Restart**. You can also run `claude auth login` in PowerShell yourself.
- **"uv is not installed".** Run `install.ps1` again, or set the path to `uv.exe` under
  Settings → Advanced.
- **"The AutoCAD tools stopped".** Open Settings → Advanced → **Open log folder** and read
  `bridge.log`.
- **A tool reports `application_busy`.** AutoCAD is in the middle of a command or showing a
  dialog. Press Esc in AutoCAD and ask again.
- **AutoCAD LT** isn't supported because it has no automation (COM) interface.

## Build from source

You need the .NET 8 SDK and uv. Run:

```bash
./build.sh
```

The script runs the bridge tests, compiles the plugin and writes
`dist/ClaudeForAutoCAD-1.0.0.zip`. To work on the bridge alone:

```bash
cd bridge
```

```bash
uv run --extra test pytest
```

Those tests drive the real AutoCAD MCP Pro server on its headless engine, with a scripted
model in place of Claude.
