using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Loupedeck.ClaudeCodePlugin.Models;

// What a session is doing; ResolveState picks the one most urgent for the user.
public enum AgentSessionState {
  Idle,
  Thinking,    // Working on a turn
  Agents,      // Subagents running (during or after the turn)
  Compacting,  // Summarizing the conversation, context full
  Permission,  // Waits for an allow/deny
  Question,    // Asks a question (AskUserQuestion, MCP elicitation), answered in the editor
  PlanReady,   // Plan mode: waits for the user to pick how to go on, in the editor
  LimitHit,    // The 5-hour or weekly limit is reached
  Complete,    // Turn finished, not looked at yet
  Error        // Turn ended on an API error
}

// How a key answers a permission request.
public enum PermissionDecision {
  AllowOnce,
  AllowAlways,    // Allow and apply the rule Claude Code suggests, so it does not ask again
  Deny
}

// A permission request the PermissionRequest hook holds until a key (or the editor) answers it
public sealed class PendingPermission {
  public string ToolName { get; init; } = "";
  public string Summary { get; init; } = "";
  public JsonArray Suggestions { get; init; }
  public DateTime ReceivedUtc { get; init; } = DateTime.UtcNow;

  // Hook output JSON; "" leaves the decision to the editor's dialog
  public TaskCompletionSource<string> Answer { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// One live session as the keys show it
public sealed class ClaudeSession {
  // ---------------------------------------------------------------- Identity

  public string Id { get; set; } = "";
  public int Pid { get; set; }
  public string DisplayName { get; set; } = "";
  public string Cwd { get; set; } = "";
  public string TranscriptPath { get; set; } = "";

  // Tiles are keyed by session: a project can have several
  public string SlotKey => Id;

  // ---------------------------------------------------------------- Status

  public AgentSessionState State { get; set; } = AgentSessionState.Idle;
  public DateTime LastUpdated { get; set; }
  public DateTime StatusSince { get; set; }
  public string Activity { get; set; } = "";       // tool in use
  public PendingPermission Pending { get; set; }
  public int QueuedMessages { get; set; }         // keypad messages not delivered yet

  public bool CanAnswer => State == AgentSessionState.Permission;
  public bool NeedsUser =>
      State is AgentSessionState.Permission or AgentSessionState.Question or
          AgentSessionState.PlanReady;
  public bool IsWorking =>
      State is AgentSessionState.Thinking or AgentSessionState.Agents or
          AgentSessionState.Compacting;

  // ---------------------------------------------------------------- Model and usage

  public string Model { get; set; } = "";   // e.g. claude-opus-5-5
  public string Effort { get; set; } = "";  // e.g. xhigh

  public long InputTokens { get; set; }
  public long OutputTokens { get; set; }
  public long CacheReadTokens { get; set; }
  public long ContextUsedTokens { get; set; }
  public long ContextMaxTokens { get; set; }

  // Context in use: as reported, else input plus cache-read tokens.
  public long TotalContextTokens =>
      ContextUsedTokens > 0 ? ContextUsedTokens : InputTokens + CacheReadTokens;

  // Share of the context window in use; without a reported size, 200k or 1M is assumed.
  public double ContextUsagePercent {
    get {
      var used = TotalContextTokens;
      var window = ContextMaxTokens > 0 ? ContextMaxTokens : used > 200_000 ? 1_000_000 : 200_000;
      return Math.Min(100.0, used * 100.0 / window);
    }
  }

  // Model family and effort, e.g. "Opus · xhigh".
  public string ModelLabel {
    get {
      var id = Model.ToLowerInvariant();
      string family = null;
      foreach (var name in new[] { "opus", "sonnet", "haiku", "fable" }) {
        if (id.Contains(name)) {
          family = char.ToUpperInvariant(name[0]) + name[1..];
        }
      }
      if (family == null) {
        return Effort;
      }

      return Effort.Length > 0 ? $"{family} · {Effort}" : family;
    }
  }

  // ---------------------------------------------------------------- Progress

  public int AgentCount { get; set; }      // subagents running
  public int AgentsStarted { get; set; }   // since the last prompt, or in the running workflow
  public int AgentsDone { get; set; }

  public int TasksDone { get; set; }
  public int TasksTotal { get; set; }
  public string CurrentTask { get; set; } = "";

  public string WorkflowPhase { get; set; } = "";
  public int WorkflowPhaseIndex { get; set; }
  public int WorkflowPhaseCount { get; set; }

  public TimeSpan? Remaining { get; set; }   // negative once the estimate is exceeded

  // While working: "3/7 · 8 min", "2/4 · 40 min", "~12 min", "+2 min", "5/8 agents"; "" otherwise
  public string ProgressText {
    get {
      if (!IsWorking) {
        return "";
      }

      var steps = TasksTotal > 0 ? $"{TasksDone}/{TasksTotal}"
          : WorkflowPhaseCount > 0 ? $"{WorkflowPhaseIndex}/{WorkflowPhaseCount}"
          : "";
      var time = "";
      if (Remaining is TimeSpan left) {
        var minutes = Math.Max(1, (int)Math.Ceiling(Math.Abs(left.TotalMinutes)));
        time = left < TimeSpan.Zero ? $"+{minutes} min" : $"{minutes} min";
      }

      if (steps.Length > 0) {
        return time.Length > 0 ? $"{steps} · {time}" : steps;
      }

      if (time.Length > 0) {
        return time.StartsWith('+') ? time : "~" + time;
      }

      return AgentsStarted > 0 ? $"{AgentsDone}/{AgentsStarted} agents" : "";
    }
  }

  // Short status shown in the pill at the bottom of a session tile.
  public string StatusPillText => State switch {
    AgentSessionState.Thinking => ProgressText.Length > 0 ? ProgressText : "Working",
    AgentSessionState.Agents => ProgressText.Length > 0 ? ProgressText
        : AgentCount > 1 ? $"{AgentCount} agents"
        : "Agent",
    AgentSessionState.Compacting => "Compacting",
    AgentSessionState.Permission => "Needs you",
    AgentSessionState.Question => "Question",
    AgentSessionState.PlanReady => "Plan ready",
    AgentSessionState.LimitHit => "Limit hit",
    AgentSessionState.Complete => "Done",
    AgentSessionState.Error => "Error",
    _ => "Idle"
  };
}

// One window of the plan's usage limits
public sealed class LimitWindow {
  public double UsedPercent { get; set; }
  public DateTime? ResetsAtUtc { get; set; }

  public TimeSpan? TimeLeft =>
      ResetsAtUtc is DateTime at
          ? (at > DateTime.UtcNow ? at - DateTime.UtcNow : TimeSpan.Zero)
          : null;
}

// The 5-hour and weekly limits of the Claude plan
public sealed class PlanLimits {
  public LimitWindow FiveHour { get; set; } = new();
  public LimitWindow SevenDay { get; set; } = new();

  // False until Claude Code reported them once
  public bool IsLive { get; set; }
}
