using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentPad.Shared;

// The parts of AgentPad's setup the plugin and the hook executable both need: where things live,
// how AgentPad's entries in ~/.claude/settings.json are recognized, and how a JSON settings file is
// read and safely rewritten.
public static class HookSettings {
  public const string HookExeName = "claude-keypad-hook.exe";
  private const string BackupSuffix = ".logi-keypad.bak";

  // %LOCALAPPDATA%\AgentPad: the deployed hook and the "hooks removed" marker. The hook lives
  // here, outside the plugin package, so Claude Code still finds it (and it still exits quietly)
  // after the plugin itself is uninstalled.
  public static string DataDir => Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentPad");

  // Present when the user removed the hooks: the plugin then leaves Claude Code's settings alone.
  public static string RemovedMarker => Path.Combine(DataDir, "hooks-removed");

  public static string ClaudeSettingsPath => Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

  private static readonly JsonDocumentOptions ReadOptions = new() {
    CommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true
  };

  private static readonly JsonSerializerOptions WriteOptions = new() {
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
  };

  // Whether a hook entry or status line runs AgentPad's hook (bare path or quoted command line).
  public static bool IsOurs(JsonNode entry) {
    var command = Text(entry?["command"]) ?? "";
    return command.EndsWith(HookExeName, StringComparison.OrdinalIgnoreCase) ||
        command.Contains(HookExeName + "\"", StringComparison.OrdinalIgnoreCase);
  }

  // Removes every AgentPad hook group and the AgentPad status line. Events left without hooks are
  // dropped, as is an empty "hooks" object. Returns whether anything was removed.
  public static bool RemoveOurs(JsonObject root) {
    var changed = false;
    if (root["hooks"] is JsonObject hooks) {
      foreach (var (eventName, node) in hooks.ToList()) {
        if (node is not JsonArray groups) {
          continue;
        }
        foreach (var group in groups.OfType<JsonObject>().ToList()) {
          if (group["hooks"] is JsonArray inner && inner.Any(IsOurs)) {
            groups.Remove(group);
            changed = true;
          }
        }
        if (groups.Count == 0) {
          hooks.Remove(eventName);
        }
      }
      if (hooks.Count == 0) {
        root.Remove("hooks");
      }
    }
    if (IsOurs(root["statusLine"])) {
      root.Remove("statusLine");
      changed = true;
    }
    return changed;
  }

  // Parses a JSON settings file (comments and trailing commas allowed); a missing or empty file
  // reads as `empty`.
  public static JsonNode Read(string path, string empty, out string original) {
    original = File.Exists(path) ? File.ReadAllText(path) : empty;
    return JsonNode.Parse(string.IsNullOrWhiteSpace(original) ? empty : original,
        documentOptions: ReadOptions);
  }

  // Writes `updated` when it differs from `original` as JSON. The first change keeps a backup next
  // to the file, and the new content is swapped in so no reader ever sees a half-written file.
  public static bool WriteIfChanged(string path, string original, JsonNode updated) {
    var text = updated.ToJsonString(WriteOptions);
    if (JsonNode.DeepEquals(JsonNode.Parse(original, documentOptions: ReadOptions),
        JsonNode.Parse(text))) {
      return false;
    }
    if (File.Exists(path) && !File.Exists(path + BackupSuffix)) {
      File.Copy(path, path + BackupSuffix);
    }
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    var temp = path + ".tmp";
    File.WriteAllText(temp, text);
    File.Move(temp, path, overwrite: true);
    return true;
  }

  // Text of a JSON value, or null when it is missing or not a string (a hand-edited file may hold
  // anything there).
  public static string Text(JsonNode node) =>
      node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
