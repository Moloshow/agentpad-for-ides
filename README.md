# AgentPad for IDEs

**Claude Code in your IDE, on your MX Keypad.** A Logi Actions plugin that puts your Claude Code sessions on the
Logitech MX Creative Keypad (Windows). It works with the Claude Code extension in VS Code and VS Code-based IDEs
(Cursor, Windsurf, Antigravity), and with Claude Code in the terminal.

> Unofficial community plugin. Not affiliated with or endorsed by Anthropic, Logitech or Microsoft.

## What the keys do

**Session tiles (up to 16).** Each running session gets a tile with its project name and a colored status:

| Status | Color |
|---|---|
| Working | pulsing terracotta |
| Needs you | amber |
| Question | blue |
| Plan ready | violet |
| Limit hit | rust |
| Compacting | teal |
| Agents | pulsing terracotta |
| Done | green |
| Error | red |
| Idle | charcoal |

- While Claude works, the tile shows its progress when it can, e.g. `2/4 · 40 min` for a workflow.
- Sessions fill the first tiles. Empty tiles show shortcuts instead.

**Session folder.** Press a tile to open a page of keys for that session. The page changes with what the session is doing:

- **Asking permission:**
  - Allow, Deny, Allow always.
  - What it asks for, e.g. `Bash: git status`.
  - Denials with a reason, e.g. "explain first".
- **Working:**
  - Ready-made messages, such as "time left?" or "status update". Claude gets them at its next tool call, or when its turn ends.
  - "Time left?" first shows what the plugin already knows (workflow phases, task list) and asks Claude only when nothing is known.
- **Done or idle:** ready-made messages (summarize, continue, run the tests, review, commit…). They wake Claude up even while a workflow runs in the background.
- **Every state:** context use with the model and effort in use, e.g. `Opus · xhigh`, plus tokens.

**Other keys:**
- Session count.
- Allow / Deny / Allow always: dark until a request waits.
- Waiting: jumps to the session that needs you.
- New chat.
- Editor.
- Refresh.
- 5h and 7d plan limits, with their reset times.

Messages are never typed into the editor: everything goes through Claude Code hooks.

## Install

1. Install **Logi Options+** and the **Claude Code** extension in your editor (or the Claude Code CLI).
2. Download `AgentPad_<version>.lplug4` from the [releases](https://github.com/Moloshow/agentpad-for-ides/releases)
   and double-click it to install the plugin in Logi Options+.
3. Import a profile from `profiles/`: Options+ → MX Keypad → **+** → **Import profile**.
   - `AgentPad - General.lp5`: sessions and shortcut board, in every application.
   - `AgentPad - <editor>.lp5`: the same pages plus editor and code shortcuts, for Antigravity IDE,
     VS Code, Cursor or Windsurf.
4. Open a **new** Claude Code session: hooks are read when a session starts.

Nothing else is needed: the hook is self-contained, no .NET install required.

## Uninstall

1. Press the **Claude Code hooks** key twice (group *Setup* in Options+), or run:
   `%LOCALAPPDATA%\AgentPad\hook-*\claude-keypad-hook.exe --uninstall`.
   This removes AgentPad's hooks and status line from `~/.claude/settings.json` (and, from the key,
   its editor keybindings). The same key reinstalls them.
2. Uninstall the plugin in Logi Options+.
3. Delete `%LOCALAPPDATA%\AgentPad` and `%APPDATA%\ClaudeCodeSessions` if you want no trace left.

## How it works

**Sessions** are read from the files Claude Code keeps in `~/.claude/sessions`. Sessions whose editor tab was replaced are hidden: the plugin reads the list of open tabs from the editor's workspace storage.

**Hooks.** On load, the plugin copies its hook (a small self-contained program) to
`%LOCALAPPDATA%\AgentPad` and registers it in `~/.claude/settings.json`. The hook talks to the
plugin over a local named pipe. If the plugin is not running, or was uninstalled, it exits at once
and Claude Code carries on.

| Hook | Behavior |
|---|---|
| `PermissionRequest` | Held for up to 10 minutes, so the keypad can answer. Claude Code shows its own dialog at the same time; whichever answers first wins. Questions and plans are not held: they need a choice in the editor. |
| `PostToolUse`, `PostToolUseFailure`, `Stop` | Wait for the plugin (about 80 ms) and pass on keypad messages. |
| `asyncRewake` on `Stop` and on `Notification` (`idle_prompt`) | Waits in the background, so a keypad message can wake an idle Claude. |
| All others | Run in the background. |

**Plan limits.** The editor extensions don't run the status line, so the plugin asks Claude Code itself. It runs a tiny `claude -p` request:

- Haiku, low effort, no tools, skills, MCP servers or settings;
- about 930 input and 4 output tokens;
- it reads the `rate_limit_event` from the output.

The check runs at startup, then every 10 minutes while sessions are open, and when you press a limit key (at most once a minute).

**Workflows.** Progress comes from the workflow's script (its phases) and journal (agents started and finished), next to the transcript.

**Editor keybindings.** The plugin adds a few `Ctrl+Alt+Shift+F13…F19` keybindings to every editor it finds. They focus Claude, open a new Claude tab, toggle comments, open the terminal and run quick fix. Punctuation keys move around between keyboard layouts, so function keys are used instead.

**Backups.** The first change to each file keeps a backup next to it (`*.logi-keypad.bak`).

**Ready-made messages** live in `%APPDATA%\ClaudeCodeSessions\phrases.json`. Edit the labels and texts there; changes apply at once.

## Privacy

No personal data is collected and everything stays on your computer. The plugin reads Claude Code's local session files, transcripts and workflow files, and talks only to its own hook process. The only network use is the plan-limit check, a tiny request made by your Claude Code CLI under your own account (it counts toward your plan usage).

Full details: [Privacy Policy](PRIVACY.md).

## Building

Requirements:

- .NET 10 SDK
- Logi Options+, because the build references `PluginApi.dll` from the Logi Plugin Service
- Python 3 with Pillow, for the icon and profile scripts

```bash
dotnet build -c Release src                    # plugin + self-contained hook; links and reloads it
logiplugintool pack bin/Release dist/AgentPad_1_0_0.lplug4
logiplugintool verify dist/AgentPad_1_0_0.lplug4
python tools/make_icons.py                     # action icons
python tools/make_profiles.py                  # profiles/*.lp5
```

`logiplugintool` comes from `dotnet tool install --global LogiPluginTool`.

## License

The plugin's license (EULA) is the [MIT License](LICENSE). See also the [Privacy Policy](PRIVACY.md).

Claude and Claude Code are trademarks of Anthropic. Logitech, Logi and MX are trademarks of Logitech.
Visual Studio Code is a trademark of Microsoft.

[MIT](LICENSE) © 2026 [Moloshow](https://github.com/Moloshow) · [arthuraries.com](https://arthuraries.com)
