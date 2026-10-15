# AgentPad for IDEs: Privacy Policy

_Last updated: 2026-10-15_

AgentPad for IDEs ("the plugin") is an open-source Logi Actions plugin by Moloshow. This policy
explains what it reads, what it changes on your computer and what, if anything, leaves it.

## Summary

- The plugin collects **no personal data**. It has no server, no analytics, no telemetry and no
  account.
- Everything it reads stays on your computer.
- Its only network use is the plan-limit check described below. That check is a request made by the
  Claude Code CLI already installed on your computer, under your own Claude account.

## What the plugin reads (locally)

- Claude Code's session files (`~/.claude/sessions`), transcripts and workflow files
  (`~/.claude/projects`), to show each session's status, progress, context and token use.
- The open Claude Code tabs stored by your editor (VS Code, Cursor, Windsurf, Antigravity) in its
  workspace storage, to hide sessions that are no longer open.
- Hook events Claude Code sends to the plugin's own hook program over a local named pipe that only
  your Windows user can open.

The plugin keeps none of this data. It only holds it in memory while it runs.

## What the plugin changes on your computer

- `~/.claude/settings.json`: adds its hooks and, when you have none, a status line.
- `keybindings.json` of each VS Code-based editor found: adds `Ctrl+Alt+Shift+F13…F19` bindings.
- `%LOCALAPPDATA%\AgentPad`: a copy of its hook program.
- `%APPDATA%\ClaudeCodeSessions\phrases.json`: your ready-made messages.

Only the plugin's own entries are added or removed. A backup of each file is kept the first time it
is changed (`*.logi-keypad.bak`). The **Claude Code hooks** key, or
`claude-keypad-hook.exe --uninstall`, removes the hooks, status line and keybindings.

## Network use

To show your 5-hour and weekly plan limits, the plugin runs the Claude Code CLI with a tiny prompt
(Haiku model, about 930 input and 4 output tokens). It runs at startup, then every 10 minutes while
sessions are open, and when you press a limit key (at most once a minute). The request goes from the
Claude Code CLI to Anthropic under your Claude account and counts toward your plan usage, like any
other Claude Code request. Anthropic's own terms and privacy policy apply to it. The plugin itself
sends nothing anywhere.

Messages you send from the keypad are passed to Claude Code through its hooks, as if you had typed
them in your session.

## Third parties

Unofficial community plugin, not affiliated with or endorsed by Anthropic, Logitech or Microsoft.
Claude and Claude Code are trademarks of Anthropic. Logitech, Logi and MX are trademarks of Logitech.
Visual Studio Code is a trademark of Microsoft.

## Contact

Questions or concerns: [open an issue](https://github.com/Moloshow/agentpad-for-ides/issues).
