using System;
using System.Linq;
using Loupedeck.ClaudeCodePlugin.Helpers;
using Loupedeck.ClaudeCodePlugin.Models;
using Loupedeck.ClaudeCodePlugin.Services;

namespace Loupedeck.ClaudeCodePlugin.Actions;

// What an empty session tile or a shortcut board key shows and does.
public enum Shortcut {
  None,
  FiveHourLimit,
  WeeklyLimit,
  FiveHourReset,
  Context,
  Tokens,
  Allow,
  Deny,
  AllowAlways,
  Editor,
  NewChat,
  Waiting
}

// The shortcuts that fill empty session slots (page 1) and the shortcut board (page 2), in order.
// Allow / Deny / Allow Always only appear while a request waits for an answer; Editor, New Chat
// and Waiting fill what is left and give way first.
public static class Shortcuts {
  private static readonly Shortcut[] Order = {
      Shortcut.FiveHourLimit, Shortcut.WeeklyLimit, Shortcut.FiveHourReset, Shortcut.Context,
      Shortcut.Tokens, Shortcut.Allow, Shortcut.Deny, Shortcut.AllowAlways,
      Shortcut.Editor, Shortcut.NewChat, Shortcut.Waiting
  };

  private static bool IsAnswer(Shortcut s) =>
      s is Shortcut.Allow or Shortcut.Deny or Shortcut.AllowAlways;

  // The shortcut at a 0-based position of the current list, or None past its end.
  public static Shortcut At(int rank) {
    var available = ClaudeSessionService.Instance.GetPendingSession() != null
        ? Order
        : Order.Where(s => !IsAnswer(s)).ToArray();
    return rank >= 0 && rank < available.Length ? available[rank] : Shortcut.None;
  }

  // The key image of a shortcut.
  public static BitmapImage Render(Shortcut shortcut, PluginImageSize size) {
    var service = ClaudeSessionService.Instance;
    var limits = service.Limits;
    return shortcut switch {
      Shortcut.FiveHourLimit => Tiles.Limit("5h", limits.FiveHour, limits.IsLive, size),
      Shortcut.WeeklyLimit => Tiles.Limit("7d", limits.SevenDay, limits.IsLive, size),
      Shortcut.FiveHourReset => Tiles.Reset("5h", limits.FiveHour, limits.IsLive, size),
      Shortcut.Context => Tiles.Context(service.GetActiveSession(), size),
      Shortcut.Tokens => Tiles.TokenStats(service.GetActiveSession(), size),
      Shortcut.Allow => Tiles.Allow(service.GetPendingSession(), size),
      Shortcut.Deny => Tiles.Deny(service.GetPendingSession(), size),
      Shortcut.AllowAlways => Tiles.AllowAlways(service.GetPendingSession(), size),
      Shortcut.Editor => Tiles.Editor(size),
      Shortcut.NewChat => Tiles.NewChat(size),
      Shortcut.Waiting =>
          Tiles.Waiting(service.GetWaitingSessions(), PulseClock.Instance.Value, size),
      _ => Tiles.Blank(size)
    };
  }

  // What a press on a shortcut does.
  public static void Run(Shortcut shortcut) {
    var service = ClaudeSessionService.Instance;
    switch (shortcut) {
      case Shortcut.FiveHourLimit:
      case Shortcut.WeeklyLimit:
      case Shortcut.FiveHourReset:
      case Shortcut.Tokens:
        service.TriggerManualSync();
        break;
      case Shortcut.Context:
        service.OpenSession(service.GetActiveSession());
        break;
      case Shortcut.Allow:
        service.AnswerPermission(service.GetPendingSessionForKey(), PermissionDecision.AllowOnce);
        break;
      case Shortcut.Deny:
        service.AnswerPermission(service.GetPendingSessionForKey(), PermissionDecision.Deny);
        break;
      case Shortcut.AllowAlways:
        service.AnswerPermission(
            service.GetPendingSessionForKey(), PermissionDecision.AllowAlways);
        break;
      case Shortcut.Editor:
        service.FocusEditorWindow();
        break;
      case Shortcut.NewChat:
        service.StartNewConversation();
        break;
      case Shortcut.Waiting:
        service.ShowNextWaitingSession();
        break;
    }
  }
}

// Page 2: key 1 counts the sessions, keys 2-9 show the shortcuts in order, whatever the sessions.
public class ShortcutBoardCommand : PluginDynamicCommand {
  public ShortcutBoardCommand()
      : base() {
    GroupName = "SHORTCUT BOARD";
    AddParameter("1", "Board 1: active sessions", GroupName);
    for (var i = 2; i <= 9; i++) {
      AddParameter(i.ToString(), $"Board {i}: shortcut {i - 1}", GroupName);
    }

    ClaudeSessionService.Instance.OnSessionsChanged += () => ActionImageChanged();
    PulseClock.Instance.Tick += _ => {
      if (ClaudeSessionService.Instance.GetWaitingSessions().Count > 0) {
        ActionImageChanged();
      }
    };
  }

  private static Shortcut ShortcutFor(string actionParameter) =>
      int.TryParse(actionParameter, out var key) && key >= 2
          ? Shortcuts.At(key - 2)
          : Shortcut.None;

  protected override BitmapImage GetCommandImage(
      string actionParameter, PluginImageSize imageSize) =>
      Tiles.Safe(() => DrawKey(actionParameter, imageSize), imageSize);

  private BitmapImage DrawKey(string actionParameter, PluginImageSize imageSize) {
    var service = ClaudeSessionService.Instance;
    return actionParameter == "1"
        ? Tiles.SessionCount(
            service.Sessions.Count, service.GetWaitingSessions().Count, imageSize)
        : Shortcuts.Render(ShortcutFor(actionParameter), imageSize);
  }

  protected override void RunCommand(string actionParameter) {
    if (actionParameter == "1") {
      // Jump to a session that waits, else bring the editor to the front.
      if (ClaudeSessionService.Instance.GetWaitingSessions().Count > 0) {
        ClaudeSessionService.Instance.ShowNextWaitingSession();
      } else {
        ClaudeSessionService.Instance.FocusEditorWindow();
      }

      return;
    }
    Shortcuts.Run(ShortcutFor(actionParameter));
  }
}
