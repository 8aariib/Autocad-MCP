# Connect Claude Desktop and Claude Code to AutoCAD only

Claude reaches your PC through one door: the AutoCAD MCP server. It can see the
AutoCAD session you already have open and the one drawings folder you choose.
It cannot browse your other files, run programs or use the Windows shell.

## The safety settings

| Setting | What it does |
|---|---|
| `AUTOCAD_MCP_BACKEND=com` | Talks to your live AutoCAD |
| `CAD_LAUNCH=never` | Attaches only to an AutoCAD you opened yourself; never starts one |
| `ALLOWED_PATHS=...` | The only folder it may open or save drawings in |
| `MAX_TOOL_COST=destructive` | Blocks raw command-line and AutoLISP execution (`system_run_command`, `system_run_lisp`) |
| `DANGEROUS_COMMANDS_ENABLED=false` | Extra block on ERASE ALL, QUIT and similar |

For look-but-don't-touch access, add `"READ_ONLY": "true"`.

## 1. Once: install uv (Windows PowerShell)

    powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"

Close and reopen PowerShell afterwards.

## 2a. Claude Desktop

1. Claude Desktop -> Settings -> Developer -> Edit Config
   (opens `%APPDATA%\Claude\claude_desktop_config.json`).
2. Paste in `claude_desktop_config.json` from this folder. If the file already
   has other servers, add only the `"autocad"` block inside `mcpServers`.
3. Replace `YOUR_NAME` with your Windows user folder (keep the double `\\`).
4. Quit Claude Desktop completely (tray icon -> Quit) and reopen it.
5. Open AutoCAD and a drawing, then ask Claude: "What's in my current drawing?"

Do NOT turn on any "Desktop Commander" / filesystem / computer-use
connector if you want Claude limited to AutoCAD.

## 2b. Claude Code (terminal)

Run once in PowerShell (one line):

    claude mcp add autocad --scope user -e AUTOCAD_MCP_BACKEND=com -e CAD_LAUNCH=never -e "ALLOWED_PATHS=C:\Users\YOUR_NAME\Documents\AutoCAD" -e MAX_TOOL_COST=destructive -e DANGEROUS_COMMANDS_ENABLED=false -- uvx --from "autocad-mcp-pro[com,pdf,office]==1.8.0" autocad-mcp

Then start `claude` from inside your drawings folder (not your home folder),
so Claude Code's own file access is limited to that folder too, and type
`/mcp` to check that `autocad` shows as connected. Claude Code asks
before it runs shell commands or edits files, so keep those prompts on.

Optional: the drafting skill that teaches it the plan -> draw -> check ->
save workflow:

    claude plugin marketplace add U-C4N/Autocad-MCP
    claude plugin install autocad-mcp-pro@autocad-mcp

(The plugin starts its own copy of the server, so then turn off one of the
two in `/mcp`, or skip the `claude mcp add` step.)

## Troubleshooting

- "No AutoCAD session": open AutoCAD first (`CAD_LAUNCH=never` means it won't start it).
- AutoCAD busy: finish or ESC out of any running command, close dialog boxes.
- Run AutoCAD and Claude as the same Windows user, both non-admin (or both admin).
- AutoCAD LT has no COM automation, so this needs full AutoCAD or a vertical (Architecture, Mechanical, ...).
