using System;
using Loupedeck.ClaudeCodePlugin.Helpers;
using Loupedeck.ClaudeCodePlugin.Models;
using Loupedeck.ClaudeCodePlugin.Services;

namespace Loupedeck.ClaudeCodePlugin.Actions;

// A key that redraws whenever sessions or limits change. Profiles store keys by class name, so
// the class names below stay as they are.
public abstract class KeyCommand : PluginDynamicCommand {
  protected static ClaudeSessionService Service => ClaudeSessionService.Instance;

  protected KeyCommand(string name, string description, string group)
      : base(displayName: name, description: description, groupName: group) {
    Service.OnSessionsChanged += () => ActionImageChanged();
  }

  protected abstract BitmapImage Draw(PluginImageSize size);

  protected abstract void Press();

  protected override BitmapImage GetCommandImage(
      string actionParameter, PluginImageSize imageSize) =>
      Tiles.Safe(() => Draw(imageSize), imageSize);

  protected override void RunCommand(string actionParameter) => Press();
}

// ---------------------------------------------------------------- Session

// Context use of the last opened session; opens that session.
public class ContextWindowCommand : KeyCommand {
  public ContextWindowCommand()
      : base("Context", "Context use, model and effort of the last opened session", "SESSION") { }

  protected override BitmapImage Draw(PluginImageSize size) =>
      Tiles.Context(Service.GetActiveSession(), size);

  protected override void Press() =>
      Service.OpenSession(Service.GetActiveSession());
}

// Token counts of the last opened session; refreshes on press.
public class TokenUsageCommand : KeyCommand {
  public TokenUsageCommand()
      : base("Tokens", "Input, output and cache tokens of the last opened session", "SESSION") { }

  protected override BitmapImage Draw(PluginImageSize size) =>
      Tiles.TokenStats(Service.GetActiveSession(), size);

  protected override void Press() =>
      Service.TriggerManualSync();
}

// Pulses while sessions wait for the user; each press brings the next one to the front.
public class NextWaitingCommand : KeyCommand {
  public NextWaitingCommand()
      : base(
          "Waiting",
          "Glows while a session waits for you; brings it to the front, then the next one",
          "SESSION") {
    PulseClock.Instance.Tick += _ => {
      if (Service.GetWaitingSessions().Count > 0) {
        ActionImageChanged();
      }
    };
  }

  protected override BitmapImage Draw(PluginImageSize size) =>
      Tiles.Waiting(Service.GetWaitingSessions(), PulseClock.Instance.Value, size);

  protected override void Press() =>
      Service.ShowNextWaitingSession();
}

// Reads sessions and plan limits again.
public class RefreshSessionsCommand : KeyCommand {
  public RefreshSessionsCommand()
      : base("Refresh", "Reads sessions and plan limits again now", "SESSION") { }

  protected override BitmapImage Draw(PluginImageSize size) =>
      Tiles.Refresh(Service.IsSyncing, size);

  protected override void Press() =>
      Service.TriggerManualSync();
}

// ---------------------------------------------------------------- Answers

// The answer keys stay dark until a permission request waits.

// Allows the waiting permission request once.
public class AllowOnceCommand : KeyCommand {
  public AllowOnceCommand()
      : base(
          "Allow",
          "Allows the waiting request once (the session in the front window first)",
          "ANSWER") { }

  protected override BitmapImage Draw(PluginImageSize size) =>
      Tiles.Allow(Service.GetPendingSession(), size);

  protected override void Press() =>
      Service.AnswerPermission(Service.GetPendingSessionForKey(), PermissionDecision.AllowOnce);
}

// Allows the waiting permission request and keeps the suggested rule.
public class AllowConversationCommand : KeyCommand {
  public AllowConversationCommand()
      : base(
          "Allow always",
          "Allows the waiting request and keeps the rule Claude Code suggests",
          "ANSWER") { }

  protected override BitmapImage Draw(PluginImageSize size) =>
      Tiles.AllowAlways(Service.GetPendingSession(), size);

  protected override void Press() =>
      Service.AnswerPermission(Service.GetPendingSessionForKey(), PermissionDecision.AllowAlways);
}

// Denies the waiting permission request.
public class DenyCommand : KeyCommand {
  public DenyCommand()
      : base(
          "Deny",
          "Denies the waiting request (the session in the front window first)",
          "ANSWER") { }

  protected override BitmapImage Draw(PluginImageSize size) =>
      Tiles.Deny(Service.GetPendingSession(), size);

  protected override void Press() =>
      Service.AnswerPermission(Service.GetPendingSessionForKey(), PermissionDecision.Deny);
}

// ---------------------------------------------------------------- Editor

// Opens a new Claude Code tab in the front editor window.
public class NewConversationCommand : KeyCommand {
  public NewConversationCommand()
      : base("New chat", "Opens a new Claude Code tab in the front editor window", "EDITOR") { }

  protected override BitmapImage Draw(PluginImageSize size) =>
      Tiles.NewChat(size);

  protected override void Press() =>
      Service.StartNewConversation();
}

// Brings the editor of the last opened session to the front.
public class FocusEditorCommand : KeyCommand {
  public FocusEditorCommand()
      : base("Editor", "Brings the editor of the last opened session to the front", "EDITOR") { }

  protected override BitmapImage Draw(PluginImageSize size) =>
      Tiles.Editor(size);

  protected override void Press() =>
      Service.FocusEditorWindow();
}

// ---------------------------------------------------------------- Plan limits

// Used share of the 5-hour plan limit.
public class FiveHourLimitCommand : KeyCommand {
  public FiveHourLimitCommand()
      : base("5h limit", "Used share of the 5-hour limit of the Claude plan", "PLAN LIMITS") { }

  protected override BitmapImage Draw(PluginImageSize size) =>
      Tiles.Limit("5h", Service.Limits.FiveHour, Service.Limits.IsLive, size);

  protected override void Press() =>
      Service.TriggerManualSync();
}

// Used share of the weekly plan limit.
public class WeeklyLimitCommand : KeyCommand {
  public WeeklyLimitCommand()
      : base("7d limit", "Used share of the weekly limit of the Claude plan", "PLAN LIMITS") { }

  protected override BitmapImage Draw(PluginImageSize size) =>
      Tiles.Limit("7d", Service.Limits.SevenDay, Service.Limits.IsLive, size);

  protected override void Press() =>
      Service.TriggerManualSync();
}

// Time until the 5-hour plan limit resets.
public class FiveHourResetTimeCommand : KeyCommand {
  public FiveHourResetTimeCommand()
      : base("5h reset", "Time until the 5-hour limit resets", "PLAN LIMITS") { }

  protected override BitmapImage Draw(PluginImageSize size) =>
      Tiles.Reset("5h", Service.Limits.FiveHour, Service.Limits.IsLive, size);

  protected override void Press() =>
      Service.TriggerManualSync();
}

// Time until the weekly plan limit resets.
public class WeeklyResetTimeCommand : KeyCommand {
  public WeeklyResetTimeCommand()
      : base("7d reset", "Time until the weekly limit resets", "PLAN LIMITS") { }

  protected override BitmapImage Draw(PluginImageSize size) =>
      Tiles.Reset("7d", Service.Limits.SevenDay, Service.Limits.IsLive, size);

  protected override void Press() =>
      Service.TriggerManualSync();
}

// Removes AgentPad's hooks and keybindings from Claude Code and the editors (before uninstalling
// the plugin), or puts them back. Two presses within a few seconds, so it never happens by
// accident.
public class HooksSetupCommand : KeyCommand {
  private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(3);
  private DateTime _armedAt = DateTime.MinValue;

  public HooksSetupCommand()
      : base(
          "Claude Code hooks",
          "Removes AgentPad's hooks from Claude Code (press twice), or reinstalls them",
          "SETUP") {
  }

  private bool Armed => DateTime.UtcNow - _armedAt < ConfirmWindow;

  protected override BitmapImage Draw(PluginImageSize size) {
    var removed = SetupService.HooksRemoved;
    if (Armed) {
      return Tiles.Label(removed ? "Reinstall?" : "Remove?", "press again",
          removed ? Palette.GreenLight : Palette.RedLight, size);
    }
    return Tiles.Label("Hooks", removed ? "removed" : "installed",
        removed ? Palette.RedLight : Palette.GreenLight, size);
  }

  protected override void Press() {
    if (!Armed) {
      _armedAt = DateTime.UtcNow;
      ActionImageChanged();
      System.Threading.Tasks.Task.Run(async () => {
        await System.Threading.Tasks.Task.Delay(ConfirmWindow);
        ActionImageChanged();
      });
      return;
    }

    _armedAt = DateTime.MinValue;
    if (SetupService.HooksRemoved) {
      SetupService.Install();
    } else {
      SetupService.Uninstall();
    }
    ActionImageChanged();
  }
}
