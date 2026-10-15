using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Loupedeck.ClaudeCodePlugin.Helpers;

namespace Loupedeck.ClaudeCodePlugin.Services;

// Tells which Claude Code sessions are open in an editor tab. The extension keeps the list of its
// panel tabs in each editor's workspace storage (state.vscdb, key "Anthropic.claude-code",
// "panelTabSessions"). A session opened from the history in place of another one keeps its
// predecessor's process alive; this check keeps the keypad from showing a tile for a conversation
// that no tab shows.
internal static class EditorTabs {
  private const string StateKey = "Anthropic.claude-code";
  private static readonly string[] Editors = {
    "Antigravity IDE", "Antigravity", "Code", "Code - Insiders", "Cursor", "Windsurf"
  };

  // The editor writes its storage a little after a change, so a new tab may not be listed yet.
  private static readonly TimeSpan StorageLag = TimeSpan.FromSeconds(15);
  private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(5);

  private sealed class Workspace {
    public string Folder = "";
    public string DbPath = "";
    public DateTime ReadStamp = DateTime.MinValue;
    public HashSet<string> OpenSessions = new(StringComparer.OrdinalIgnoreCase);
  }

  private static readonly object Lock = new();
  private static List<Workspace> _workspaces = new();
  private static DateTime _scannedAt = DateTime.MinValue;

  // False only when the storage of the session's workspace, written well after the session
  // started, does not list it.
  public static bool IsOpen(string sessionId, string cwd, DateTime startedUtc, DateTime now) {
    lock (Lock) {
      if (now - _scannedAt > RescanInterval) {
        Rescan();
        _scannedAt = now;
      }

      var folder = Normalize(cwd);
      var matches = _workspaces.Where(w => w.Folder == folder).ToList();
      if (matches.Count == 0) {
        return true;
      }
      foreach (var w in matches) {
        if (w.OpenSessions.Contains(sessionId)) {
          return true;
        }
      }
      return !matches.Any(w => w.ReadStamp > startedUtc + StorageLag);
    }
  }

  // Re-reads the workspace storages of every editor; unchanged databases are not read again.
  private static void Rescan() {
    var found = new List<Workspace>();
    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    foreach (var editor in Editors) {
      var root = Path.Combine(appData, editor, "User", "workspaceStorage");
      if (!Directory.Exists(root)) {
        continue;
      }

      foreach (var dir in Directory.EnumerateDirectories(root)) {
        var db = Path.Combine(dir, "state.vscdb");
        var meta = Path.Combine(dir, "workspace.json");
        if (!File.Exists(db) || !File.Exists(meta)) {
          continue;
        }

        var previous = _workspaces.FirstOrDefault(
            w => string.Equals(w.DbPath, db, StringComparison.OrdinalIgnoreCase));
        var stamp = File.GetLastWriteTimeUtc(db);
        if (previous != null && previous.ReadStamp == stamp) {
          found.Add(previous);
          continue;
        }

        try {
          var folderUri = (JsonNode.Parse(File.ReadAllText(meta)) as JsonObject)?["folder"]
              ?.GetValue<string>();
          if (string.IsNullOrEmpty(folderUri)) {
            continue;
          }

          var workspace = new Workspace {
            Folder = Normalize(FolderPath(folderUri)),
            DbPath = db,
            ReadStamp = stamp
          };
          var rows = WindowsPlatform.QuerySqlite(
              db, $"SELECT value FROM ItemTable WHERE key = '{StateKey}';");
          if (rows.Count > 0 &&
              JsonNode.Parse(rows[0][0]) is JsonObject state &&
              state["panelTabSessions"] is JsonArray tabs) {
            foreach (var tab in tabs.OfType<JsonObject>()) {
              var id = tab["sessionId"]?.GetValue<string>();
              if (!string.IsNullOrEmpty(id)) {
                workspace.OpenSessions.Add(id);
              }
            }
          }
          found.Add(workspace);
        } catch (Exception) {
          // Being written, or not a folder workspace: skipped until the next scan.
        }
      }
    }
    _workspaces = found;
  }

  // "file:///c%3A/Users/x" -> "c:/Users/x" (Uri.LocalPath keeps a leading slash when the colon is
  // escaped).
  private static string FolderPath(string folderUri) =>
      Uri.UnescapeDataString(new Uri(folderUri).AbsolutePath).TrimStart('/');

  private static string Normalize(string path) =>
      (path ?? "").Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
}
