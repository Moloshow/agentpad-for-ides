using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Loupedeck.ClaudeCodePlugin.Helpers;
using Loupedeck.ClaudeCodePlugin.Models;
using Loupedeck.ClaudeCodePlugin.Services;

namespace Loupedeck.ClaudeCodePlugin.Actions;

// A session tile. With a session on it, pressing it opens a folder of keys for that session,
// arranged for what it is doing; without one, it shows (and runs) one of the shortcuts.
public abstract class SessionFolder : PluginDynamicFolder {
  private enum Layout { Working, Asking, Done, Info }

  // Folder keys after the device's Back key, row by row. W/P/D + digit = ready-made message.
  private static readonly Dictionary<Layout, string[]> Keys = new() {
    [Layout.Working] = new[] {
        "Project", "Tokens", "W0", "W1", "W2", "W3", "Activity", "Context"
    },
    [Layout.Asking] = new[] {
        "Project", "Activity", "Allow", "Deny", "AllowAlways", "P0", "P1", "Context"
    },
    [Layout.Done] = new[] {
        "Project", "Context", "D0", "D1", "D2", "D3", "D4", "D5"
    },
    [Layout.Info] = new[] {
        "Project", "Activity", "Context", "Tokens", "Off1", "Off2", "Off3", "Off4"
    },
  };

  private static readonly TimeSpan SentShown = TimeSpan.FromSeconds(2);
  private static readonly TimeSpan KnownProgressShown = TimeSpan.FromSeconds(8);

  private readonly int _slot;
  private readonly Dictionary<string, DateTime> _sentAt = new();
  private readonly Dictionary<string, DateTime> _progressShownAt = new();
  private Layout _layout = Layout.Done;
  private bool _open;

  protected SessionFolder(int slot) {
    _slot = slot;
    DisplayName = $"Session {slot}";
    GroupName = "SESSIONS";
    Service.OnSessionsChanged += Refresh;
    PulseClock.Instance.Tick += OnPulse;
  }

  private static ClaudeSessionService Service => ClaudeSessionService.Instance;

  private ClaudeSession Session => Service.GetSessionForSlot(_slot);

  // Which set of folder keys fits what the session is doing.
  private static Layout LayoutFor(ClaudeSession s) =>
      s == null ? Layout.Done
      : s.CanAnswer ? Layout.Asking
      : s.State is AgentSessionState.Question or AgentSessionState.PlanReady ? Layout.Info
      : s.IsWorking ? Layout.Working
      : Layout.Done;

  // ---------------------------------------------------------------- Empty tiles

  // Shortcut shown on this tile: empty tiles take the shortcuts in order; with no session at all,
  // tile 1 shows "No session" instead.
  private Shortcut ShortcutHere() {
    if (Session != null) {
      return Shortcut.None;
    }

    var emptyBefore =
        Enumerable.Range(1, _slot - 1).Count(n => Service.GetSessionForSlot(n) == null);
    if (Service.Sessions.Count == 0) {
      if (_slot == 1) {
        return Shortcut.None;
      }

      emptyBefore--;
    }
    return Shortcuts.At(emptyBefore);
  }

  // ---------------------------------------------------------------- Redrawing

  // Redraws this tile on the device (folder buttons are addressed by this action name).
  private void RedrawTile() =>
      Plugin?.OnActionImageChanged("#DynamicFolder", "DynamicFolder#" + GetType().FullName, false);

  // Redraws the tile and, while the folder is open, its layout and keys.
  private void Refresh() {
    RedrawTile();
    if (!_open) {
      return;
    }

    var layout = LayoutFor(Session);
    if (layout != _layout) {
      _layout = layout;
      ButtonActionNamesChanged();
    }
    foreach (var key in Keys[_layout]) {
      CommandImageChanged(key);
    }
  }

  // Animates a working session's tile, or this tile when it shows the Waiting shortcut.
  private void OnPulse(double _) {
    var session = Session;
    if (session?.IsWorking == true) {
      RedrawTile();
      if (_open) {
        CommandImageChanged("Project");
      }
    } else if (session == null && ShortcutHere() == Shortcut.Waiting &&
        Service.GetWaitingSessions().Count > 0) {
      RedrawTile();
    }
  }

  // Redraws a key now and again after a delay, to end a flash.
  private void Flash(string key, TimeSpan duration) {
    CommandImageChanged(key);
    Task.Run(async () => {
      await Task.Delay(duration);
      CommandImageChanged(key);
    });
  }

  // ---------------------------------------------------------------- Opening and closing

  // Pressing the tile: runs the shortcut of an empty tile, otherwise opens the session's folder.
  public override bool Activate() {
    var shortcut = ShortcutHere();
    if (Session == null) {
      // An empty tile is a shortcut (or just a label): run it and close the folder right away.
      if (shortcut != Shortcut.None) {
        Shortcuts.Run(shortcut);
      }
      Task.Run(async () => {
        await Task.Delay(50);
        Close();
      });
      return base.Activate();
    }

    _open = true;
    _layout = LayoutFor(Session);
    Service.CurrentActiveSlot = _slot;
    Service.HoldSlot(_slot);
    var result = base.Activate();

    // The device may have asked for the keys before the session's state was known.
    ButtonActionNamesChanged();
    Refresh();
    return result;
  }

  public override bool Deactivate() {
    _open = false;
    Service.ReleaseSlot(_slot);
    return base.Deactivate();
  }

  public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType _) =>
      PluginDynamicFolderNavigation.None;

  // The device's Back key, then the keys of the current layout.
  public override string[] GetButtonPressActionNames(DeviceType deviceType) =>
      new[] { PluginDynamicFolder.NavigateUpActionName }
          .Concat(Keys[_layout].Select(k => CreateCommandName(k)))
          .ToArray();

  // ---------------------------------------------------------------- Images

  public override BitmapImage GetButtonImage(PluginImageSize size) =>
      Tiles.Safe(() => DrawTile(size), size);

  private BitmapImage DrawTile(PluginImageSize size) {
    var shortcut = ShortcutHere();
    return Session == null && shortcut != Shortcut.None
        ? Shortcuts.Render(shortcut, size)
        : Tiles.Session(_slot, Session, PulseClock.Instance.Value, size, inFolder: true);
  }

  public override BitmapImage GetCommandImage(string key, PluginImageSize size) =>
      Tiles.Safe(() => DrawKey(key, size), size);

  private BitmapImage DrawKey(string key, PluginImageSize size) {
    var session = Session;
    var phrase = PhraseFor(key);
    if (phrase != null) {
      if (Recently(_progressShownAt, key, KnownProgressShown)) {
        return Tiles.KnownProgress(session, size);
      }

      return Tiles.Phrase(phrase.Label, key[0], Recently(_sentAt, key, SentShown), size);
    }

    return key switch {
      "Project" => Tiles.Session(_slot, session, PulseClock.Instance.Value, size),
      "Activity" => Tiles.Activity(session, size),
      "Context" => Tiles.Context(session, size),
      "Tokens" => Tiles.TokenStats(session, size),
      "Allow" => Tiles.Allow(session, size),
      "Deny" => Tiles.Deny(session, size),
      "AllowAlways" => Tiles.AllowAlways(session, size),
      _ => Tiles.Blank(size)
    };
  }

  private static bool Recently(Dictionary<string, DateTime> times, string key, TimeSpan window) =>
      times.TryGetValue(key, out var at) && DateTime.UtcNow - at < window;

  // ---------------------------------------------------------------- Presses

  // A press on a folder key: sends a phrase, answers a request, opens or refreshes the session.
  public override void RunCommand(string key) {
    var session = Session;
    var phrase = PhraseFor(key);
    if (phrase != null && session != null) {
      // "Time left?" first shows what is already known (workflow, task list, earlier estimate).
      if (phrase.Text.Contains("[ETA", StringComparison.OrdinalIgnoreCase) &&
          KnowsProgress(session)) {
        _progressShownAt[key] = DateTime.UtcNow;
        Flash(key, KnownProgressShown);
        return;
      }
      Service.SendPhrase(session, phrase.Text);
      _sentAt[key] = DateTime.UtcNow;
      Flash(key, SentShown);
      return;
    }

    switch (key) {
      case "Allow" when session != null:
        Service.AnswerPermission(session, PermissionDecision.AllowOnce);
        break;
      case "Deny" when session != null:
        Service.AnswerPermission(session, PermissionDecision.Deny);
        break;
      case "AllowAlways" when session != null:
        Service.AnswerPermission(session, PermissionDecision.AllowAlways);
        break;
      case "Project":
      case "Context":
      case "Activity":
        Service.OpenSession(session);
        break;
      case "Tokens":
        Service.TriggerManualSync();
        break;
    }
  }

  private static bool KnowsProgress(ClaudeSession s) =>
      s.WorkflowPhaseCount > 0 || s.TasksTotal > 0 || s.Remaining.HasValue;

  // W/P/D + index -> the phrase of that group, or null for other keys.
  private static PhraseService.Phrase PhraseFor(string key) {
    if (key.Length != 2 || !char.IsDigit(key[1])) {
      return null;
    }

    var all = PhraseService.Current;
    var list = key[0] switch {
      'W' => all.Working,
      'P' => all.Permission,
      'D' => all.Done,
      _ => null
    };
    return list == null ? null : PhraseService.Get(list, key[1] - '0');
  }
}

// The 16 tiles. Profiles refer to them by these class names.
public sealed class SessionSlot01DynamicFolder : SessionFolder {
  public SessionSlot01DynamicFolder() : base(1) { }
}

public sealed class SessionSlot02DynamicFolder : SessionFolder {
  public SessionSlot02DynamicFolder() : base(2) { }
}

public sealed class SessionSlot03DynamicFolder : SessionFolder {
  public SessionSlot03DynamicFolder() : base(3) { }
}

public sealed class SessionSlot04DynamicFolder : SessionFolder {
  public SessionSlot04DynamicFolder() : base(4) { }
}

public sealed class SessionSlot05DynamicFolder : SessionFolder {
  public SessionSlot05DynamicFolder() : base(5) { }
}

public sealed class SessionSlot06DynamicFolder : SessionFolder {
  public SessionSlot06DynamicFolder() : base(6) { }
}

public sealed class SessionSlot07DynamicFolder : SessionFolder {
  public SessionSlot07DynamicFolder() : base(7) { }
}

public sealed class SessionSlot08DynamicFolder : SessionFolder {
  public SessionSlot08DynamicFolder() : base(8) { }
}

public sealed class SessionSlot09DynamicFolder : SessionFolder {
  public SessionSlot09DynamicFolder() : base(9) { }
}

public sealed class SessionSlot10DynamicFolder : SessionFolder {
  public SessionSlot10DynamicFolder() : base(10) { }
}

public sealed class SessionSlot11DynamicFolder : SessionFolder {
  public SessionSlot11DynamicFolder() : base(11) { }
}

public sealed class SessionSlot12DynamicFolder : SessionFolder {
  public SessionSlot12DynamicFolder() : base(12) { }
}

public sealed class SessionSlot13DynamicFolder : SessionFolder {
  public SessionSlot13DynamicFolder() : base(13) { }
}

public sealed class SessionSlot14DynamicFolder : SessionFolder {
  public SessionSlot14DynamicFolder() : base(14) { }
}

public sealed class SessionSlot15DynamicFolder : SessionFolder {
  public SessionSlot15DynamicFolder() : base(15) { }
}

public sealed class SessionSlot16DynamicFolder : SessionFolder {
  public SessionSlot16DynamicFolder() : base(16) { }
}
