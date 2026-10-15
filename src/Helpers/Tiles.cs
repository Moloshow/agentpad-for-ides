using System;
using System.Collections.Generic;
using System.Linq;
using Loupedeck.ClaudeCodePlugin.Models;

namespace Loupedeck.ClaudeCodePlugin.Helpers;

// Colors of the keypad keys.
internal static class Palette {
  public static readonly BitmapColor Base = new(38, 38, 36);           // Warm charcoal.
  public static readonly BitmapColor BaseLight = new(70, 66, 60);
  public static readonly BitmapColor Black = new(0, 0, 0);
  public static readonly BitmapColor White = new(255, 255, 255);
  public static readonly BitmapColor Soft = new(222, 214, 200);
  public static readonly BitmapColor Muted = new(160, 152, 140);
  public static readonly BitmapColor Ink = new(24, 24, 27);

  public static readonly BitmapColor Terracotta = new(217, 119, 87);   // Working, pulse peak.
  public static readonly BitmapColor TerracottaDark = new(140, 62, 40);
  public static readonly BitmapColor Amber = new(234, 179, 8);         // Needs you.
  public static readonly BitmapColor Green = new(22, 163, 74);         // Done.
  public static readonly BitmapColor GreenLight = new(34, 197, 94);
  public static readonly BitmapColor Red = new(185, 28, 28);           // Error, deny.
  public static readonly BitmapColor RedLight = new(239, 68, 68);
  public static readonly BitmapColor Blue = new(37, 99, 235);          // Question.
  public static readonly BitmapColor BlueDeep = new(30, 64, 175);
  public static readonly BitmapColor Violet = new(124, 58, 237);       // Plan.
  public static readonly BitmapColor Rust = new(154, 52, 18);          // Limit reached.
  public static readonly BitmapColor Teal = new(15, 118, 110);         // Compacting.
  public static readonly BitmapColor TealDark = new(10, 80, 74);
  public static readonly BitmapColor Track = new(24, 24, 22);

  // Linear blend from a (t = 0) to b (t = 1).
  public static BitmapColor Mix(BitmapColor a, BitmapColor b, double t) {
    t = Math.Clamp(t, 0, 1);
    return new BitmapColor(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
  }

  public static BitmapColor Darker(BitmapColor c, double factor = 0.55) =>
      new((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

  // Green under 50 %, amber under 80 %, red above.
  public static BitmapColor ForUsage(double percent) =>
      percent < 50 ? GreenLight : percent < 80 ? Amber : RedLight;
}

// One key image: a background, lines of text stacked in the middle, and a few shapes.
internal sealed class KeyCanvas : IDisposable {
  private readonly BitmapBuilder _builder;

  public KeyCanvas(PluginImageSize size, BitmapColor background) {
    _builder = new BitmapBuilder(size);
    _builder.Clear(background);
  }

  public BitmapBuilder Builder => _builder;
  public int W => _builder.Width;
  public int H => _builder.Height;
  public bool Big => H >= 100;

  // Font size for this key; sizes are given for the big (116 px) keys.
  public int Font(int big) => Big ? big : Math.Max(9, (int)Math.Round(big * 0.78));

  // Lines of (text, font, color) centered as one block between top and bottom.
  public void Stack(
      IReadOnlyList<(string Text, int Font, BitmapColor Color)> lines, int top = 0,
      int bottom = -1) {
    if (bottom < 0) {
      bottom = H;
    }

    var heights = lines.Select(l => l.Font + 5).ToList();
    var y = top + Math.Max(0, (bottom - top - heights.Sum()) / 2);
    for (var i = 0; i < lines.Count; i++) {
      if (!string.IsNullOrEmpty(lines[i].Text)) {
        _builder.DrawText(
            lines[i].Text, 2, y, W - 4, heights[i], lines[i].Color, lines[i].Font);
      }
      y += heights[i];
    }
  }

  // Text broken into lines that fit the key, at most maxLines (the last one shortened).
  public List<string> Wrap(string text, int font, int maxLines) {
    var perLine = Math.Max(4, (int)((W - 8) / (font * 0.56)));
    var lines = new List<string>();
    var current = "";
    foreach (var word in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
      var piece = word;
      while (piece.Length > perLine) {
        if (current.Length > 0) {
          lines.Add(current);
          current = "";
        }
        lines.Add(piece[..perLine]);
        piece = piece[perLine..];
      }
      if (current.Length == 0) {
        current = piece;
      } else if (current.Length + 1 + piece.Length <= perLine) {
        current += " " + piece;
      } else {
        lines.Add(current);
        current = piece;
      }
    }
    if (current.Length > 0) {
      lines.Add(current);
    }

    if (lines.Count > maxLines) {
      lines = lines.Take(maxLines).ToList();
      var last = lines[^1];
      lines[^1] = (last.Length >= perLine ? last[..(perLine - 1)] : last).TrimEnd() + "…";
    }
    return lines;
  }

  // Horizontally centered bar.
  public void Bar(int y, int width, int height, BitmapColor color) =>
      _builder.FillRectangle((W - width) / 2, y, width, height, color);

  // Rounded label at the bottom of a session tile.
  public void Pill(string text, BitmapColor fill, BitmapColor textColor) {
    var h = Big ? 24 : 18;
    var w = W - (Big ? 14 : 8);
    var x = (W - w) / 2;
    var y = H - h - (Big ? 6 : 3);
    var r = h / 2;
    _builder.FillRectangle(x + r, y, w - 2 * r, h, fill);
    _builder.FillCircle(x + r, y + r, r, fill);
    _builder.FillCircle(x + w - r, y + r, r, fill);
    _builder.DrawText(text, x, y + 1, w, h - 1, textColor, Font(16));
  }

  // Top edge of the pill drawn by Pill.
  public int PillTop => H - (Big ? 24 : 18) - (Big ? 6 : 3);

  // Ring around a center: a dark track and the used share, clockwise from the top.
  public void Ring(int cx, int cy, int radius, double percent, BitmapColor color) {
    var thickness = Big ? 7 : 5;
    _builder.DrawArc(cx, cy, radius, 0, 360, Palette.Track, thickness + 1);
    if (percent > 0) {
      var sweep = (float)(360 * Math.Clamp(percent, 0, 100) / 100);
      _builder.DrawArc(cx, cy, radius, -90, sweep, color, thickness);
    }
  }

  public void Check(int cx, int cy, int size, BitmapColor color, float thickness = 3.5f) {
    _builder.DrawLine(cx - size, cy, cx - size / 3, cy + size * 2 / 3, color, thickness);
    _builder.DrawLine(
        cx - size / 3, cy + size * 2 / 3, cx + size, cy - size * 2 / 3, color, thickness);
  }

  public void Cross(int cx, int cy, int size, BitmapColor color, float thickness = 3.5f) {
    _builder.DrawLine(cx - size, cy - size, cx + size, cy + size, color, thickness);
    _builder.DrawLine(cx - size, cy + size, cx + size, cy - size, color, thickness);
  }

  public BitmapImage Image() => _builder.ToImage();

  public void Dispose() => _builder.Dispose();
}

// The images of every key the plugin draws.
internal static class Tiles {
  private static string _lastError = "";

  // Draws with a fallback: a failed image is logged once and replaced by a plain red key.
  public static BitmapImage Safe(Func<BitmapImage> draw, PluginImageSize size) {
    try {
      return draw();
    } catch (Exception ex) {
      var where = ex.StackTrace?.Split((char)10).FirstOrDefault()?.Trim();
      var message = $"{ex.GetType().Name}: {ex.Message} at {where}";
      if (message != _lastError) {
        _lastError = message;
        PluginLog.Error($"[Draw] {message}");
      }
      using var key = new KeyCanvas(size, Palette.Red);
      return key.Image();
    }
  }

  // ---------------------------------------------------------------- Sessions

  private static BitmapColor StateColor(AgentSessionState state, double pulse) => state switch {
    AgentSessionState.Thinking or AgentSessionState.Agents =>
        Palette.Mix(Palette.TerracottaDark, Palette.Terracotta, pulse),
    AgentSessionState.Compacting => Palette.Mix(Palette.TealDark, Palette.Teal, pulse),
    AgentSessionState.Permission => Palette.Amber,
    AgentSessionState.Question => Palette.Blue,
    AgentSessionState.PlanReady => Palette.Violet,
    AgentSessionState.LimitHit => Palette.Rust,
    AgentSessionState.Complete => Palette.Green,
    AgentSessionState.Error => Palette.Red,
    _ => Palette.Base
  };

  // Project name on top, status pill at the bottom; amber tiles use dark text.
  public static BitmapImage Session(
      int slot, ClaudeSession session, double pulse, PluginImageSize size,
      bool inFolder = false) {
    if (session == null) {
      using var empty = new KeyCanvas(size, Palette.Base);
      var first = slot == 1 ? "No session" : $"Slot {slot}";
      var second = slot == 1 ? "active" : "Empty";
      empty.Stack(new[] {
          (first, empty.Font(16), Palette.Soft),
          (second, empty.Font(15), Palette.Muted)
      });
      return empty.Image();
    }

    var background = StateColor(session.State, pulse);
    var dark = session.State == AgentSessionState.Permission;
    var text = dark ? Palette.Ink : Palette.White;
    using var key = new KeyCanvas(size, background);

    if (!inFolder) {
      // Small mark at the top, where the device draws its folder tab.
      key.Bar(0, 30, 2, dark ? Palette.Darker(Palette.Amber) : Palette.White);
    }

    var font = key.Font(17);
    var lines = key.Wrap(session.DisplayName, font, 2);
    if (lines.Count == 2 && lines.Max(l => l.Length) > 10) {
      font = key.Font(15);
      lines = key.Wrap(session.DisplayName, font, 2);
    }
    var top = inFolder ? (key.Big ? 12 : 8) : 4;
    key.Stack(lines.Select(l => (l, font, text)).ToList(), top, key.PillTop - 2);
    key.Pill(session.StatusPillText, Palette.Darker(background, dark ? 0.2 : 0.5), Palette.White);
    return key.Image();
  }

  // "3 sessions active"; amber with how many wait when some do.
  public static BitmapImage SessionCount(int count, int waiting, PluginImageSize size) {
    var amber = waiting > 0;
    using var key = new KeyCanvas(size, amber ? Palette.Amber : Palette.Base);
    var text = amber ? Palette.Ink : Palette.White;
    var noun = count == 1 ? "session" : "sessions";
    var detail = amber
        ? $"{waiting} waiting"
        : "active";
    key.Stack(new[] {
        (count.ToString(), key.Font(30), text),
        (noun, key.Font(14), text),
        (detail, key.Font(14), amber ? Palette.Ink : Palette.Soft)
    });
    return key.Image();
  }

  // ---------------------------------------------------------------- Gauges

  private static string Tokens(long n) =>
      n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 1000 ? $"{n / 1000}k" : n.ToString();

  // Title, a ring with the percentage inside, a caption under it.
  private static BitmapImage Gauge(
      PluginImageSize size, string title, string caption, double percent,
      BitmapColor ringColor, bool known) {
    using var key = new KeyCanvas(size, Palette.Base);
    var titleFont = key.Font(14);
    var captionFont = key.Font(13);
    var radius = key.Big ? 27 : 19;
    var titleH = titleFont + 5;
    var captionH = captionFont + 5;
    var block = titleH + 2 * radius + 8 + captionH;
    var top = Math.Max(0, (key.H - block) / 2);
    var cy = top + titleH + radius + 4;

    key.Builder.DrawText(title, 2, top, key.W - 4, titleH, Palette.Soft, titleFont);
    key.Ring(key.W / 2, cy, radius, known ? percent : 0, ringColor);
    var value = known ? $"{Math.Round(percent):0}%" : "–";
    key.Builder.DrawText(
        value, 0, cy - radius / 2 - 2, key.W, radius + 4, Palette.White, key.Font(18));
    key.Builder.DrawText(
        caption, 2, cy + radius + 4, key.W - 4, captionH, Palette.Muted, captionFont);
    return key.Image();
  }

  // Context use of a session, titled with its model and effort when known ("Opus · xhigh").
  public static BitmapImage Context(ClaudeSession session, PluginImageSize size) {
    var title = string.IsNullOrEmpty(session?.ModelLabel) ? "Context" : session.ModelLabel;
    return Gauge(
        size, title, Tokens(session?.TotalContextTokens ?? 0),
        session?.ContextUsagePercent ?? 0, Palette.White, session != null);
  }

  // Used share of a plan window ("5h", "7d").
  public static BitmapImage Limit(
      string window, LimitWindow quota, bool live, PluginImageSize size) =>
      Gauge(
          size, $"Claude {window}", live ? Until(quota) : "…", quota?.UsedPercent ?? 0,
          Palette.ForUsage(quota?.UsedPercent ?? 0), live);

  // Time until a plan window resets, and the local reset time.
  public static BitmapImage Reset(
      string window, LimitWindow quota, bool live, PluginImageSize size) {
    using var key = new KeyCanvas(size, Palette.Base);
    var at = live && quota?.ResetsAtUtc != null
        ? quota.ResetsAtUtc.Value.ToLocalTime().ToString("HH:mm")
        : "";
    key.Stack(new[] {
        ($"{window} reset", key.Font(14), Palette.Soft),
        (live ? Until(quota) : "…", key.Font(24), Palette.White),
        (at, key.Font(13), Palette.Muted)
    });
    return key.Image();
  }

  private static string Until(LimitWindow quota) {
    var left = quota?.TimeLeft;
    if (left == null) {
      return "–";
    }

    var t = left.Value;
    return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}"
        : $"{Math.Max(0, t.Minutes)} min";
  }

  // Input, output and cache tokens of a session.
  public static BitmapImage TokenStats(ClaudeSession session, PluginImageSize size) {
    using var key = new KeyCanvas(size, Palette.Base);
    if (session == null) {
      key.Stack(new[] {
          ("Tokens", key.Font(15), Palette.Soft),
          ("–", key.Font(20), Palette.White)
      });
      return key.Image();
    }
    key.Stack(new[] {
        ("Tokens", key.Font(14), Palette.Soft),
        ($"in {Tokens(session.InputTokens)}", key.Font(15), Palette.White),
        ($"out {Tokens(session.OutputTokens)}", key.Font(15), Palette.White),
        ($"cache {Tokens(session.CacheReadTokens)}", key.Font(14), Palette.Muted)
    });
    return key.Image();
  }

  // ---------------------------------------------------------------- Answers

  // Black key, shown while nothing waits for an answer.
  public static BitmapImage Blank(PluginImageSize size) {
    using var key = new KeyCanvas(size, Palette.Black);
    return key.Image();
  }

  public static BitmapImage Allow(ClaudeSession session, PluginImageSize size) =>
      session?.CanAnswer == true
          ? Answer(
              size, "Allow", "once", Palette.Amber, Palette.Ink,
              (k, y) => k.Check(k.W / 2, y, k.Big ? 10 : 8, Palette.Ink))
          : Blank(size);

  public static BitmapImage AllowAlways(ClaudeSession session, PluginImageSize size) =>
      session?.CanAnswer == true
          ? Answer(size, "Allow", "always", Palette.Amber, Palette.Ink, (k, y) => {
            var s = k.Big ? 8 : 6;
            k.Check(k.W / 2 - s - 2, y, s, Palette.Ink, 3f);
            k.Check(k.W / 2 + s + 2, y, s, Palette.Ink, 3f);
          })
          : Blank(size);

  public static BitmapImage Deny(ClaudeSession session, PluginImageSize size) =>
      session?.CanAnswer == true
          ? Answer(
              size, "Deny", "", Palette.Red, Palette.White,
              (k, y) => k.Cross(k.W / 2, y, k.Big ? 9 : 7, Palette.White))
          : Blank(size);

  // An icon in the upper part, two words under it.
  private static BitmapImage Answer(
      PluginImageSize size, string word, string detail, BitmapColor background,
      BitmapColor text, Action<KeyCanvas, int> icon) {
    using var key = new KeyCanvas(size, background);
    var iconY = key.Big ? 30 : 22;
    icon(key, iconY);
    key.Stack(
        new[] { (word, key.Font(19), text), (detail, key.Font(15), text) },
        iconY + (key.Big ? 14 : 10));
    return key.Image();
  }

  // Pulses while sessions wait for you: how many, and the first one.
  public static BitmapImage Waiting(
      IReadOnlyList<ClaudeSession> waiting, double pulse, PluginImageSize size) {
    if (waiting.Count == 0) {
      using var idle = new KeyCanvas(size, Palette.Base);
      idle.Stack(new[] {
          ("Waiting", idle.Font(15), Palette.Soft),
          ("none", idle.Font(15), Palette.Muted)
      });
      return idle.Image();
    }
    using var key = new KeyCanvas(
        size, Palette.Mix(Palette.Darker(Palette.Amber, 0.75), Palette.Amber, pulse));
    var name = key.Wrap(waiting[0].DisplayName, key.Font(13), 1).FirstOrDefault() ?? "";
    key.Stack(new[] {
        ("Waiting", key.Font(14), Palette.Ink),
        (waiting.Count.ToString(), key.Font(26), Palette.Ink),
        (name, key.Font(13), Palette.Ink)
    });
    return key.Image();
  }

  // ---------------------------------------------------------------- Commands

  // Two lines over a colored bar.
  public static BitmapImage Label(
      string first, string second, BitmapColor accent, PluginImageSize size) {
    using var key = new KeyCanvas(size, Palette.Base);
    var lines = new List<(string, int, BitmapColor)> { (first, key.Font(16), Palette.White) };
    if (!string.IsNullOrEmpty(second)) {
      lines.Add((second, key.Font(14), Palette.Soft));
    }

    var barGap = key.Big ? 10 : 7;
    key.Stack(lines, 0, key.H - barGap - 4);
    key.Bar(key.H / 2 + (lines.Count * (key.Font(16) + 5)) / 2 + 2, 30, 4, accent);
    return key.Image();
  }

  // A green plus over "New chat".
  public static BitmapImage NewChat(PluginImageSize size) {
    using var key = new KeyCanvas(size, Palette.Base);
    var arm = key.Big ? 11 : 8;
    var cy = key.H / 2 - (key.Big ? 12 : 8);
    key.Builder.DrawLine(key.W / 2 - arm, cy, key.W / 2 + arm, cy, Palette.GreenLight, 4);
    key.Builder.DrawLine(key.W / 2, cy - arm, key.W / 2, cy + arm, Palette.GreenLight, 4);
    key.Stack(new[] { ("New chat", key.Font(16), Palette.White) }, cy + arm + 2);
    return key.Image();
  }

  public static BitmapImage Editor(PluginImageSize size) =>
      Label("Editor", "open", Palette.Blue, size);

  public static BitmapImage Refresh(bool busy, PluginImageSize size) =>
      Label(
          busy ? "Refreshing" : "Refresh", busy ? "…" : "now",
          busy ? Palette.Amber : Palette.GreenLight, size);

  // A ready-made message, its bar colored by kind (W working, P denial, D done); green for a
  // moment once sent.
  public static BitmapImage Phrase(
      string label, char kind, bool justSent, PluginImageSize size) {
    if (justSent) {
      using var sent = new KeyCanvas(size, Palette.Green);
      sent.Stack(new[] { (kind == 'W' ? "Queued" : "Sent", sent.Font(17), Palette.White) });
      return sent.Image();
    }
    using var key = new KeyCanvas(size, Palette.Base);
    var accent = kind == 'W' ? Palette.Terracotta
        : kind == 'P' ? Palette.RedLight
        : Palette.GreenLight;
    var font = key.Font(16);
    var lines = key.Wrap(label, font, 2);
    key.Stack(lines.Select(l => (l, font, Palette.White)).ToList(), 0, key.H - 12);
    key.Bar(key.H - (key.Big ? 18 : 13), 30, 4, accent);
    return key.Image();
  }

  // ---------------------------------------------------------------- What a session does

  // What the session asks for (amber), or what it is doing, its progress and queued messages.
  public static BitmapImage Activity(ClaudeSession session, PluginImageSize size) {
    var (title, detail) = Describe(session);
    if (session?.QueuedMessages > 0 && session.Pending == null) {
      detail = session.QueuedMessages == 1
          ? "1 queued message"
          : $"{session.QueuedMessages} queued messages";
    }

    var asking = session?.State == AgentSessionState.Permission;
    using var key = new KeyCanvas(size, asking ? Palette.Amber : Palette.Base);
    var text = asking ? Palette.Ink : Palette.White;
    var font = key.Font(13);
    var lines = new List<(string, int, BitmapColor)> { (title, key.Font(15), text) };
    lines.AddRange(
        key.Wrap(detail, font, 3).Select(l => (l, font, asking ? Palette.Ink : Palette.Soft)));
    key.Stack(lines);
    return key.Image();
  }

  private static (string Title, string Detail) Describe(ClaudeSession s) {
    if (s == null) {
      return ("Activity", "No session");
    }

    if (s.Pending != null) {
      return ("Asks", string.IsNullOrEmpty(s.Pending.Summary)
          ? s.Pending.ToolName
          : $"{s.Pending.ToolName}: {s.Pending.Summary}");
    }
    return s.State switch {
      AgentSessionState.Permission => ("Asks", "see editor"),
      AgentSessionState.Question => ("Question", "answer in the editor"),
      AgentSessionState.PlanReady => ("Plan", "choose in the editor"),
      AgentSessionState.LimitHit => ("Limit hit", "wait for the reset"),
      _ when s.IsWorking && s.ProgressText.Length > 0 => (s.ProgressText, ProgressDetail(s)),
      AgentSessionState.Compacting => ("Compacting", "context is full"),
      AgentSessionState.Agents => ("Agents", $"{s.AgentCount} running"),
      AgentSessionState.Thinking =>
          ("Doing", string.IsNullOrEmpty(s.Activity) ? "Thinking" : s.Activity),
      _ => ("Activity", s.StatusPillText)
    };
  }

  private static string ProgressDetail(ClaudeSession s) =>
      !string.IsNullOrEmpty(s.CurrentTask) ? s.CurrentTask
      : s.WorkflowPhaseCount > 0 ? $"{s.WorkflowPhase} · {s.AgentsDone}/{s.AgentsStarted} agents"
      : s.Activity;

  // Progress the plugin already knows, shown instead of asking Claude.
  public static BitmapImage KnownProgress(ClaudeSession session, PluginImageSize size) {
    using var key = new KeyCanvas(size, Palette.BlueDeep);
    var title = string.IsNullOrEmpty(session?.ProgressText) ? "No estimate" : session.ProgressText;
    var font = key.Font(13);
    var lines = new List<(string, int, BitmapColor)> { (title, key.Font(17), Palette.White) };
    if (session != null) {
      lines.AddRange(
          key.Wrap(ProgressDetail(session), font, 3).Select(l => (l, font, Palette.Soft)));
    }
    key.Stack(lines);
    return key.Image();
  }
}
