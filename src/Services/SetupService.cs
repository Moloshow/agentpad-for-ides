using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AgentPad.Shared;

namespace Loupedeck.ClaudeCodePlugin.Services;

// Connects AgentPad to Claude Code and the editors, and disconnects it on request:
//  - deploys the hook executable to %LOCALAPPDATA%\AgentPad (outside the plugin package, so Claude
//    Code keeps finding a quiet hook even after the plugin is uninstalled);
//  - registers it as hooks and status line in ~/.claude/settings.json;
//  - adds the keybindings the keys send to every installed VS Code-based editor.
// Only AgentPad's own entries are added, updated or removed; each file is backed up the first time.
public static class SetupService {
  // How long a PermissionRequest stays answerable from the keypad. Claude Code shows its own
  // dialog at the same time, so holding it longer costs nothing; whichever answers first wins.
  public const int PermissionHoldSeconds = 600;

  // Must match "name" in LoupedeckPackage.yaml and the plugin class name (minus "Plugin").
  public const string PluginName = "ClaudeCodeSessions";

  // Hooks that never change what Claude Code does run in the background.
  private static readonly string[] AsyncEvents = {
    "SessionStart", "UserPromptSubmit", "PreToolUse",
    "PermissionDenied", "Notification", "StopFailure", "SessionEnd",
    "PreCompact", "PostCompact", "SubagentStart", "SubagentStop"
  };

  // Editor shortcuts the keys send: Ctrl+Alt+Shift+F13..F19 are free in every editor.
  public static readonly (string Key, string Command, string When)[] Keybindings = {
    ("ctrl+alt+shift+f13", "claude-vscode.focus", null),
    ("ctrl+alt+shift+f14", "claude-vscode.editor.open", null),
    ("ctrl+alt+shift+f15", "claude-vscode.acceptProposedDiff", null),
    ("ctrl+alt+shift+f16", "claude-vscode.rejectProposedDiff", null),

    // Toggles the comment of the selection, or of the cursor's line (like Ctrl+/, whatever the
    // keyboard layout).
    ("ctrl+alt+shift+f17", "editor.action.commentLine", "editorTextFocus"),

    // Punctuation keys move around between keyboard layouts (Ctrl+` is not the terminal on a UK
    // keyboard), so the keys send function-key shortcuts instead.
    ("ctrl+alt+shift+f18", "workbench.action.terminal.toggleTerminal", null),
    ("ctrl+alt+shift+f19", "editor.action.quickFix",
        "editorHasCodeActionsProvider && textInputFocus && !editorReadonly")
  };

  // Folders under %APPDATA% of VS Code-based editors the Claude Code extension runs in.
  private static readonly string[] EditorFolders = {
    "Antigravity IDE", "Antigravity", "Code", "Code - Insiders", "Cursor", "Windsurf"
  };

  // Whether the user removed AgentPad from Claude Code (keypad key or `--uninstall`).
  public static bool HooksRemoved => File.Exists(HookSettings.RemovedMarker);

  // On plugin load: connects everything, unless the user removed the hooks.
  public static void Run() {
    if (HooksRemoved) {
      PluginLog.Info("[Setup] Hooks were removed by the user, Claude Code is left alone");
      return;
    }
    Install();
  }

  // Deploys the hook, then registers hooks and keybindings; failures are logged, not thrown.
  public static void Install() {
    try {
      if (File.Exists(HookSettings.RemovedMarker)) {
        File.Delete(HookSettings.RemovedMarker);
      }
      var exe = DeployHook();
      if (exe == null) {
        PluginLog.Warning("[Setup] The hook executable is missing from the plugin package");
      } else {
        InstallClaudeHooks(exe);
      }
    } catch (Exception ex) {
      PluginLog.Warning($"[Setup] Claude Code hooks not installed: {ex.Message}");
    }

    foreach (var editor in EditorFolders) {
      try {
        UpdateKeybindings(editor, add: true);
      } catch (Exception ex) {
        PluginLog.Warning($"[Setup] Keybindings for {editor} not installed: {ex.Message}");
      }
    }
  }

  // Removes AgentPad's hooks, status line and keybindings, and remembers it so the next plugin load
  // does not put them back. The deployed hook stays: running hooks may still hold it open.
  public static void Uninstall() {
    try {
      Directory.CreateDirectory(HookSettings.DataDir);
      File.WriteAllText(HookSettings.RemovedMarker, DateTime.UtcNow.ToString("o"));

      var path = HookSettings.ClaudeSettingsPath;
      if (HookSettings.Read(path, "{}", out var original) is JsonObject root &&
          HookSettings.RemoveOurs(root) && HookSettings.WriteIfChanged(path, original, root)) {
        PluginLog.Info($"[Setup] Removed the Claude Code hooks from {path}");
      }
    } catch (Exception ex) {
      PluginLog.Warning($"[Setup] Claude Code hooks not removed: {ex.Message}");
    }

    foreach (var editor in EditorFolders) {
      try {
        UpdateKeybindings(editor, add: false);
      } catch (Exception ex) {
        PluginLog.Warning($"[Setup] Keybindings for {editor} not removed: {ex.Message}");
      }
    }
  }

  // ---------------------------------------------------------------- Hook executable

  // The hook folder of the plugin package (hook\, beside bin\ so the plugin service does not load
  // the hook's trimmed runtime as plugin dependencies), found the way the Logi Plugin Service finds
  // the plugin: the installed package, or the .link file of a development build. The service loads
  // the plugin DLL from memory, so Assembly.Location cannot tell.
  private static string PackageHookDir() {
    var plugins = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Logi", "LogiPluginService", "Plugins");
    var installed = Path.Combine(plugins, PluginName, "hook");
    if (File.Exists(Path.Combine(installed, HookSettings.HookExeName))) {
      return installed;
    }

    var link = Path.Combine(plugins, PluginName + ".link");
    if (File.Exists(link)) {
      var linked = Path.GetFullPath(Path.Combine(File.ReadAllText(link).Trim(), "hook"));
      if (File.Exists(Path.Combine(linked, HookSettings.HookExeName))) {
        return linked;
      }
    }
    return null;
  }

  // Copies the packaged hook to %LOCALAPPDATA%\AgentPad\hook-<version> and returns its exe. Each
  // build gets its own folder: hooks waiting in the background keep the previous one open, so it
  // could not be overwritten. Older folders are removed once nothing holds them.
  private static string DeployHook() {
    var source = PackageHookDir();
    if (source == null) {
      return null;
    }

    var sourceExe = new FileInfo(Path.Combine(source, HookSettings.HookExeName));
    var version = $"{sourceExe.LastWriteTimeUtc.Ticks:x}-{sourceExe.Length:x}";
    var target = Path.Combine(HookSettings.DataDir, "hook-" + version);
    var targetExe = Path.Combine(target, HookSettings.HookExeName);

    if (!File.Exists(targetExe)) {
      var staging = target + ".tmp";
      if (Directory.Exists(staging)) {
        Directory.Delete(staging, recursive: true);
      }
      Directory.CreateDirectory(staging);
      foreach (var file in Directory.EnumerateFiles(source)) {
        File.Copy(file, Path.Combine(staging, Path.GetFileName(file)));
      }
      Directory.Move(staging, target);
      PluginLog.Info($"[Setup] Hook deployed to {target}");
    }

    foreach (var old in Directory.EnumerateDirectories(HookSettings.DataDir, "hook-*")
                 .Where(d => !string.Equals(d, target, StringComparison.OrdinalIgnoreCase))) {
      try {
        Directory.Delete(old, recursive: true);
      } catch (Exception) {
        // Still in use by a hook waiting in the background: removed on a later load.
      }
    }
    return targetExe;
  }

  // ---------------------------------------------------------------- Claude Code

  private static void InstallClaudeHooks(string exe) {
    var path = HookSettings.ClaudeSettingsPath;
    var root = HookSettings.Read(path, "{}", out var original) as JsonObject ??
        throw new InvalidDataException("settings.json is not a JSON object");

    // Start from a clean slate: AgentPad's entries are rewritten, everything else is kept.
    HookSettings.RemoveOurs(root);
    if (root["hooks"] is not JsonObject hooks) {
      hooks = new JsonObject();
      root["hooks"] = hooks;
    }

    foreach (var eventName in
        AsyncEvents.Concat(HookServer.AnsweredEvents).Where(e => e != "Rewake")) {
      var isPermission = eventName == "PermissionRequest";
      var isAnswered = Array.IndexOf(HookServer.AnsweredEvents, eventName) >= 0;
      var hook = new JsonObject {
        ["type"] = "command",
        ["command"] = exe,
        ["args"] = new JsonArray(eventName),
        ["timeout"] = isPermission ? PermissionHoldSeconds + 10 : 10
      };
      if (!isAnswered) {
        hook["async"] = true;
      }
      Groups(hooks, eventName).Add(new JsonObject { ["hooks"] = new JsonArray(hook) });
    }

    // Wake-up hooks wait in the background so a keypad message can wake Claude. One starts when a
    // turn ends; another when Claude Code reports it waits for input, which also covers an
    // interrupted turn (an interruption fires no Stop).
    foreach (var (eventName, matcher) in
        new[] { ("Stop", (string)null), ("Notification", "idle_prompt") }) {
      var group = new JsonObject {
        ["hooks"] = new JsonArray(new JsonObject {
          ["type"] = "command",
          ["command"] = exe,
          ["args"] = new JsonArray("Rewake"),
          ["asyncRewake"] = true
        })
      };
      if (matcher != null) {
        group["matcher"] = matcher;
      }
      Groups(hooks, eventName).Add(group);
    }

    // Status line: only when there is none, so a user's own status line is never replaced.
    if (root["statusLine"] == null) {
      root["statusLine"] = new JsonObject {
        ["type"] = "command",
        ["command"] = $"\"{exe.Replace('\\', '/')}\" statusline"
      };
    }

    if (HookSettings.WriteIfChanged(path, original, root)) {
      PluginLog.Info($"[Setup] Updated the Claude Code hooks in {path}");
    }
  }

  // The hook groups of an event, created when missing.
  private static JsonArray Groups(JsonObject hooks, string eventName) {
    if (hooks[eventName] is not JsonArray groups) {
      groups = new JsonArray();
      hooks[eventName] = groups;
    }
    return groups;
  }

  // ---------------------------------------------------------------- Editors

  // Adds (or removes) AgentPad's keybindings in one editor's keybindings.json.
  private static void UpdateKeybindings(string editorFolder, bool add) {
    var userDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), editorFolder, "User");
    if (!Directory.Exists(userDir)) {
      return;
    }

    var path = Path.Combine(userDir, "keybindings.json");
    var list = HookSettings.Read(path, "[]", out var original) as JsonArray ??
        throw new InvalidDataException("keybindings.json is not a JSON array");

    foreach (var (key, command, when) in Keybindings) {
      var existing = list.OfType<JsonObject>().Where(b =>
          string.Equals(HookSettings.Text(b["key"]), key, StringComparison.OrdinalIgnoreCase) &&
          string.Equals(HookSettings.Text(b["command"]), command, StringComparison.Ordinal))
          .ToList();
      if (add && existing.Count == 1 && HookSettings.Text(existing[0]["when"]) == when) {
        continue;
      }

      // Missing, duplicated or with another condition: replace with a single binding.
      foreach (var old in existing) {
        list.Remove(old);
      }
      if (add) {
        var binding = new JsonObject { ["key"] = key, ["command"] = command };
        if (when != null) {
          binding["when"] = when;
        }
        list.Add(binding);
      }
    }

    if (HookSettings.WriteIfChanged(path, original, list)) {
      PluginLog.Info($"[Setup] Updated the {editorFolder} keybindings in {path}");
    }
  }
}
