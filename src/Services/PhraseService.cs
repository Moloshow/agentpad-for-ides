using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Loupedeck.ClaudeCodePlugin.Services;

// Ready-made messages the session folder sends to Claude. They live in a JSON file the user can
// edit (%APPDATA%\ClaudeCodeSessions\phrases.json); it is created with defaults and re-read when
// it changes.
public static class PhraseService {
  // A message: the key label and the text sent to Claude.
  public sealed class Phrase {
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
  }

  // The messages offered for each state of a session.
  public sealed class PhraseSet {
    // While Claude works: added to its context at the next tool call (or when it stops).
    [JsonPropertyName("working")] public List<Phrase> Working { get; set; } = new();

    // While it asks for permission: denies the request with this text as the reason.
    [JsonPropertyName("permission")] public List<Phrase> Permission { get; set; } = new();

    // When it is done or idle: typed into the Claude Code panel and sent.
    [JsonPropertyName("done")] public List<Phrase> Done { get; set; } = new();
  }

  public static readonly string FilePath = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
      "ClaudeCodeSessions", "phrases.json");

  private static readonly JsonSerializerOptions Options = new() {
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
  };

  private static readonly object Lock = new();
  private static PhraseSet _current;
  private static DateTime _stamp = DateTime.MinValue;

  // The current phrases: the file is written with the defaults when missing and re-read when its
  // timestamp changes; the defaults (or the last good read) are used when it cannot be read.
  public static PhraseSet Current {
    get {
      lock (Lock) {
        try {
          if (!File.Exists(FilePath)) {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Defaults(), Options));
          }
          var stamp = File.GetLastWriteTimeUtc(FilePath);
          if (_current == null || stamp != _stamp) {
            _current = JsonSerializer.Deserialize<PhraseSet>(File.ReadAllText(FilePath), Options)
                ?? Defaults();
            _stamp = stamp;
          }
        } catch (Exception ex) {
          PluginLog.Warning($"[Phrases] {FilePath} unreadable, using defaults: {ex.Message}");
          _current ??= Defaults();
        }
        return _current;
      }
    }
  }

  // The phrase at index in the list, or null when out of range.
  public static Phrase Get(List<Phrase> list, int index) =>
      index >= 0 && index < list.Count ? list[index] : null;

  private static PhraseSet Defaults() => new() {
    Working = new() {
      new() {
        Label = "Time left?",
        Text = "Estimate the time left to finish what you are doing, answer on one line in the " +
            "exact format [ETA: N min | Step: what you are doing], then go on."
      },
      new() {
        Label = "Status update",
        Text = "Give a quick status update: where you are, what is left, and an estimate of the " +
            "time left. Then go on."
      },
      new() {
        Label = "Keep it lean",
        Text = "Focus on what matters most and leave the polish for later."
      },
      new() {
        Label = "Test at the end",
        Text = "When you are done, run the tests and fix whatever breaks."
      }
    },
    Permission = new() {
      new() {
        Label = "Why?",
        Text = "First explain why you need to do this, and suggest an alternative."
      },
      new() { Label = "Another way", Text = "Not like this, find another approach." }
    },
    Done = new() {
      new() {
        Label = "Summarize",
        Text = "Summarize in 5 lines what you did and what is left."
      },
      new() { Label = "Continue", Text = "Continue." },
      new() { Label = "Run tests", Text = "Run the tests and fix whatever fails." },
      new() {
        Label = "Review",
        Text = "Review your changes like a demanding reviewer and fix the problems."
      },
      new() { Label = "Commit", Text = "Make a commit with a clear message." },
      new() { Label = "Next?", Text = "What do you suggest as the next step?" }
    }
  };
}
