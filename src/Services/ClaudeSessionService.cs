using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Loupedeck.ClaudeCodePlugin.Helpers;
using Loupedeck.ClaudeCodePlugin.Models;

namespace Loupedeck.ClaudeCodePlugin.Services;

// Live Claude Code sessions: the session files Claude Code keeps in ~/.claude/sessions (one per
// running session, with its busy/idle status) merged with what the hooks report (permission
// requests, tool in use, finished turns), the context usage from the transcripts and the plan
// limits from the status line.
public class ClaudeSessionService : IDisposable {
  private static readonly Lazy<ClaudeSessionService> _instance =
      new(() => new ClaudeSessionService());
  public static ClaudeSessionService Instance => _instance.Value;

  private const string QuestionTool = "AskUserQuestion";
  private const string PlanTool = "ExitPlanMode";

  private static readonly TimeSpan StaleHookState = TimeSpan.FromMinutes(10);
  private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
  private static readonly TimeSpan TranscriptInterval = TimeSpan.FromSeconds(2);
  private static readonly TimeSpan LivenessInterval = TimeSpan.FromSeconds(5);

  private static readonly string ClaudeDir = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
  private static readonly string SessionsDir = Path.Combine(ClaudeDir, "sessions");
  private static readonly string ProjectsDir = Path.Combine(ClaudeDir, "projects");

  private readonly object _lock = new();
  private readonly Timer _pollTimer;
  private readonly HookServer _hookServer;
  private readonly QuotaProbeService _quotaProbe;
  private readonly SlotBoard _board = new();

  // What the hooks told us per session id; survives session file rewrites
  private readonly Dictionary<string, HookState> _hookStates =
      new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, TranscriptState> _transcripts =
      new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, ProgressTracker> _progress =
      new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, (DateTime ReadAt, WorkflowTracker.Progress Progress)>
      _workflows = new(StringComparer.OrdinalIgnoreCase);
  private static readonly TimeSpan WorkflowInterval = TimeSpan.FromSeconds(3);
  private readonly Dictionary<int, (bool Alive, DateTime CheckedAt)> _liveness = new();

  private Dictionary<int, ClaudeSession> _sessionsBySlot = new();
  private List<ClaudeSession> _sessions = new();
  private PlanLimits _quota = new();
  private string _lastChangeSignature = "";
  private string _heldKey;
  private int _isPolling;
  private int _nextWaitingIndex;
  private string _lastPhraseKey = "";
  private DateTime _lastPhraseAt = DateTime.MinValue;
  private static readonly TimeSpan PhraseDebounce = TimeSpan.FromMilliseconds(1500);

  // Slot of the session the keypad last opened
  public int CurrentActiveSlot { get; set; } = 1;

  // True while a manual sync re-reads everything
  public bool IsSyncing { get; private set; }

  // Raised whenever anything shown on the tiles changes
  public event Action OnSessionsChanged;

  // Session state as reported by the hooks and the status line
  private sealed class HookState {
    public string TranscriptPath = "";
    public string Activity = "";
    public bool AskedPermission;   // Notification permission_prompt: Claude Code shows its dialog
    public bool Completed;         // Stop: the turn finished, until the next prompt or a look at it
    public bool Failed;            // StopFailure
    public bool AskingQuestion;    // AskUserQuestion or an MCP elicitation is open
    public bool PlanShown;         // ExitPlanMode: the plan waits for approval
    public bool LimitHit;          // StopFailure because of the rate limit
    public bool Compacting;        // Between PreCompact and PostCompact
    public int Subagents;          // SubagentStart minus SubagentStop
    public int SubagentsStarted;   // Since the last prompt, for "5/8 agents"
    public int SubagentsDone;

    // Keypad messages for Claude, delivered at the next tool call or stop
    public readonly List<string> Queued = new();

    // Background hook waiting since the turn ended
    public TaskCompletionSource<string> Rewake;

    // Effort level Claude Code reports with every hook
    public string Effort = "";

    public PendingPermission Pending;
    public DateTime LastEventUtc = DateTime.UtcNow;
    public long StatusLineContextTokens;
    public long StatusLineContextMax;
  }

  // What has been read from a session's transcript so far
  private sealed class TranscriptState {
    public string Path = "";
    public long Offset;
    public DateTime ReadAt = DateTime.MinValue;
    public readonly HashSet<string> SeenMessages = new();
    public long Input, Output, CacheRead, CacheCreation, ContextUsed;

    // The last answer was a rate-limit error (until a normal answer follows)
    public bool LimitHit;

    // Model of the last answer
    public string Model = "";
  }

  // One file in ~/.claude/sessions
  private sealed class SessionFile {
    public int Pid;
    public string SessionId = "";
    public string Cwd = "";
    public string Name = "";
    public bool NameIsDerived = true;
    public string Status = "";
    public string Kind = "";
    public string Entrypoint = "";
    public DateTime StartedUtc;
    public DateTime UpdatedUtc;
  }

  public ClaudeSessionService() {
    _hookServer = new HookServer(OnHookEvent);
    _quotaProbe = new QuotaProbeService(() => Sessions.Count > 0, quota => {
      lock (_lock) {
        _quota = quota;
      }
      Scan(forceNotify: false);
    });
    _pollTimer = new Timer(OnPollTick, null, 100, (int)PollInterval.TotalMilliseconds);
  }

  // Snapshot of the sessions shown on the tiles
  public IReadOnlyList<ClaudeSession> Sessions {
    get {
      lock (_lock) {
        return _sessions.ToList();
      }
    }
  }

  // Plan usage limits from the status line or the quota probe
  public PlanLimits Limits {
    get {
      lock (_lock) {
        return _quota;
      }
    }
  }

  public ClaudeSession GetSessionForSlot(int slotNumber) {
    lock (_lock) {
      return _sessionsBySlot.TryGetValue(slotNumber, out var session) ? session : null;
    }
  }

  public ClaudeSession GetActiveSession() =>
      GetSessionForSlot(CurrentActiveSlot) ?? GetSessionForSlot(1);

  // ---------------------------------------------------------------- Slots

  // An open session folder pins its session to the tile until it closes
  public void HoldSlot(int slot) {
    lock (_lock) {
      _heldKey = _board.KeyAt(slot);
    }
  }

  public void ReleaseSlot(int slot) {
    lock (_lock) {
      var pinnedAt = _board.SlotOf(_heldKey);
      if (pinnedAt is null || pinnedAt == slot) {
        _heldKey = null;
      }
    }
  }

  // Re-reads the transcripts and the plan limits right away
  public void TriggerManualSync() {
    _quotaProbe.Probe(force: true);
    IsSyncing = true;
    OnSessionsChanged?.Invoke();
    Task.Run(() => {
      try {
        lock (_lock) {
          foreach (var t in _transcripts.Values) {
            t.ReadAt = DateTime.MinValue;
          }
        }
        Scan(forceNotify: true);
      } catch (Exception ex) {
        PluginLog.Warning($"TriggerManualSync error: {ex.Message}");
      } finally {
        IsSyncing = false;
        OnSessionsChanged?.Invoke();
      }
    });
  }

  // ---------------------------------------------------------------- Permission

  // Session with a held permission request: the preferred one if it has one, else the one waiting
  // longest
  public ClaudeSession GetPendingSession(ClaudeSession preferred = null) {
    preferred ??= GetActiveSession();
    if (preferred?.CanAnswer == true) {
      return preferred;
    }

    return GetWaitingSessions().FirstOrDefault(s => s.CanAnswer);
  }

  // Session a global Allow/Deny key answers: one waiting in the editor window in front, else the
  // last opened slot if it waits, else the session waiting longest
  public ClaudeSession GetPendingSessionForKey() {
    var foreground = WindowsPlatform.ForegroundEditorWindow();
    if (foreground != IntPtr.Zero) {
      var inFront = GetWaitingSessions()
          .FirstOrDefault(s => s.CanAnswer && EditorWindow(s) == foreground);
      if (inFront != null) {
        return inFront;
      }
    }
    return GetPendingSession();
  }

  // Sessions that need the user, longest waiting first
  public List<ClaudeSession> GetWaitingSessions() =>
      Sessions.Where(s => s.NeedsUser).OrderBy(s => s.StatusSince).ToList();

  // Answers the held permission request of the session; without one, opens its editor instead
  public void AnswerPermission(
      ClaudeSession session, PermissionDecision decision, string denyReason = null) {
    if (session == null) {
      return;
    }

    PendingPermission pending;
    lock (_lock) {
      pending = _hookStates.TryGetValue(session.Id, out var hs) ? hs.Pending : null;
    }

    if (pending == null) {
      // Nothing is held (the hold ran out): the editor's own dialog is answered there
      PluginLog.Info($"[Permission] No held request for {session.DisplayName}, opening the editor");
      OpenSession(session);
      return;
    }

    var decisionNode = new JsonObject();
    switch (decision) {
      case PermissionDecision.Deny:
        decisionNode["behavior"] = "deny";
        decisionNode["message"] = denyReason ?? "Denied from the MX Keypad";
        break;
      case PermissionDecision.AllowAlways:
        decisionNode["behavior"] = "allow";
        if (pending.Suggestions is { Count: > 0 }) {
          decisionNode["updatedPermissions"] = pending.Suggestions.DeepClone();
        }
        break;
      default:
        decisionNode["behavior"] = "allow";
        break;
    }

    var output = new JsonObject {
      ["hookSpecificOutput"] = new JsonObject {
        ["hookEventName"] = "PermissionRequest",
        ["decision"] = decisionNode
      }
    };
    PluginLog.Info($"[Permission] {decision} for {pending.ToolName} in {session.DisplayName}");
    pending.Answer.TrySetResult(output.ToJsonString());
  }

  // Sends a ready-made message: added to Claude's context while it works, given as the reason of a
  // denial while it asks for permission, typed into its panel otherwise
  public void SendPhrase(ClaudeSession session, string text) {
    if (session == null || string.IsNullOrWhiteSpace(text)) {
      return;
    }

    // One key press can reach the plugin twice; the same message to the same session within a
    // moment counts once
    lock (_lock) {
      var key = session.Id + "\n" + text;
      if (key == _lastPhraseKey && DateTime.UtcNow - _lastPhraseAt < PhraseDebounce) {
        return;
      }

      _lastPhraseKey = key;
      _lastPhraseAt = DateTime.UtcNow;
    }

    if (session.CanAnswer) {
      AnswerPermission(session, PermissionDecision.Deny, text);
      return;
    }

    lock (_lock) {
      if (!_hookStates.TryGetValue(session.Id, out var hs)) {
        hs = new HookState();
        _hookStates[session.Id] = hs;
      }

      // The main agent finished its turn (a workflow or agents may still run): wake it with the
      // message
      if (hs.Rewake != null && hs.Rewake.TrySetResult(text)) {
        hs.Rewake = null;
        PluginLog.Info($"[Phrase] Waking {session.DisplayName}");
        return;
      }

      // Mid-turn: added to its context at the next tool result, or when the turn ends
      if (!hs.Queued.Contains(text)) {
        hs.Queued.Add(text);
      }
    }
    PluginLog.Info($"[Phrase] Queued for {session.DisplayName}");
  }

  private static string KeypadNote(IEnumerable<string> messages) =>
      string.Join("\n", messages.Select(m =>
          "Message from the user, sent from their keypad (they cannot see this note, " +
              "so answer it in your next message to them): " + m));

  // Cycles through the waiting sessions, opening the next one on each call
  public void ShowNextWaitingSession() {
    var waiting = GetWaitingSessions();
    if (waiting.Count == 0) {
      return;
    }

    var session = waiting[_nextWaitingIndex++ % waiting.Count];
    var slot = _board.SlotOf(session.SlotKey);
    if (slot.HasValue) {
      CurrentActiveSlot = slot.Value;
    }
    OpenSession(session);
    OnSessionsChanged?.Invoke();
  }

  // ---------------------------------------------------------------- Editor actions

  // Brings the editor window of the session to the front; a finished session counts as seen
  public void OpenSession(ClaudeSession session) {
    session ??= GetActiveSession();
    if (session == null) {
      return;
    }

    lock (_lock) {
      if (_hookStates.TryGetValue(session.Id, out var hs) && hs.Completed) {
        hs.Completed = false;
      }
    }
    WindowsPlatform.Focus(EditorWindow(session));
    Scan(forceNotify: true);
  }

  public void FocusEditorWindow() => OpenSession(GetActiveSession() ?? Sessions.FirstOrDefault());

  // New Claude Code tab in the editor window in front, else in the editor of the last opened slot
  public void StartNewConversation() => SendEditorShortcut(TargetEditorWindow(), 1);

  // Accepts the diff Claude proposes in the target editor window
  public void AcceptProposedDiff() => SendEditorShortcut(TargetEditorWindow(), 2);

  // Rejects the diff Claude proposes in the target editor window
  public void RejectProposedDiff() => SendEditorShortcut(TargetEditorWindow(), 3);

  private IntPtr TargetEditorWindow() {
    var foreground = WindowsPlatform.ForegroundEditorWindow();
    if (foreground != IntPtr.Zero) {
      return foreground;
    }

    var session = GetActiveSession() ?? Sessions.FirstOrDefault();
    return session != null ? EditorWindow(session) : IntPtr.Zero;
  }

  // Sends SetupService.Keybindings[index] to an editor window
  private void SendEditorShortcut(IntPtr hWnd, int index) {
    if (hWnd == IntPtr.Zero) {
      PluginLog.Info("[Editor] No editor window found for the shortcut");
      return;
    }
    WindowsPlatform.SendShortcutTo(hWnd, (ushort)(WindowsPlatform.VkF13 + index),
        WindowsPlatform.VkControl, WindowsPlatform.VkMenu, WindowsPlatform.VkShift);
  }

  private IntPtr EditorWindow(ClaudeSession session) =>
      WindowsPlatform.FindEditorWindow(
          session.Pid, Path.GetFileName(session.Cwd.TrimEnd('\\', '/')));

  // ---------------------------------------------------------------- Hooks

  // Handles one hook call from Claude Code; the returned text is the hook's output
  private async Task<string> OnHookEvent(
      string eventName, JsonObject payload, CancellationToken hookGone) {
    var sessionId = GetString(payload, "session_id");
    if (eventName == "statusline") {
      OnStatusLine(sessionId, payload);
      return "";
    }
    if (string.IsNullOrEmpty(sessionId)) {
      return "";
    }

    PendingPermission pending = null;
    string injected = null;
    TaskCompletionSource<string> rewake = null;
    lock (_lock) {
      if (!_hookStates.TryGetValue(sessionId, out var hs)) {
        hs = new HookState();
        _hookStates[sessionId] = hs;
      }
      hs.LastEventUtc = DateTime.UtcNow;
      var effort = (payload["effort"] as JsonObject)?["level"]?.ToString();
      if (!string.IsNullOrEmpty(effort)) {
        hs.Effort = effort;
      }

      var transcript = GetString(payload, "transcript_path");
      if (!string.IsNullOrEmpty(transcript)) {
        hs.TranscriptPath = transcript;
      }

      switch (eventName) {
        case "SessionStart":
        case "UserPromptSubmit":
          hs.Completed = hs.Failed = hs.AskedPermission = false;
          hs.AskingQuestion = hs.PlanShown = hs.LimitHit = hs.Compacting = false;
          hs.Activity = "";
          // Queued keypad messages stay: they reach Claude at its next tool call or stop
          hs.SubagentsStarted = hs.SubagentsDone = 0;
          hs.Rewake?.TrySetResult("");
          hs.Rewake = null;
          if (eventName == "SessionStart") {
            hs.Subagents = 0;
          }
          break;
        case "PreToolUse":
          // Task list of the main agent (subagents keep their own lists)
          if (string.IsNullOrEmpty(GetString(payload, "agent_id"))) {
            Progress(sessionId).OnTool(
                GetString(payload, "tool_name"), payload["tool_input"] as JsonObject,
                DateTime.UtcNow);
          }
          hs.Activity = GetString(payload, "tool_name");
          hs.AskedPermission = false;

          // Claude moved on to another tool: the held request was answered in the editor
          if (hs.Pending != null && string.IsNullOrEmpty(GetString(payload, "agent_id")) &&
              (hs.Pending.ToolName != hs.Activity ||
                  hs.Pending.Summary != Summarize(payload["tool_input"]))) {
            hs.Pending.Answer.TrySetResult("");
          }
          if (hs.Activity == QuestionTool) {
            hs.AskingQuestion = true;
          }

          if (hs.Activity == PlanTool) {
            hs.PlanShown = true;
          }

          break;
        case "PostToolUse":
        case "PostToolUseFailure":
        case "PermissionDenied":
          // The request was answered (here or in Claude Code's dialog); tools running in parallel
          // report too, so only the same tool and input release a held request
          hs.AskedPermission = false;
          if (GetString(payload, "tool_name") == QuestionTool) {
            hs.AskingQuestion = false;
          }

          if (GetString(payload, "tool_name") == PlanTool) {
            hs.PlanShown = false;
          }

          if (hs.Pending != null && hs.Pending.ToolName == GetString(payload, "tool_name") &&
              hs.Pending.Summary == Summarize(payload["tool_input"])) {
            hs.Pending.Answer.TrySetResult("");
          }
          break;
        case "Notification":
          switch (GetString(payload, "notification_type")) {
            case "permission_prompt":
              // A question or a plan also raises it; those keep their own status
              if (!hs.AskingQuestion && !hs.PlanShown) {
                hs.AskedPermission = true;
              }
              break;
            case "elicitation_dialog":
            case "elicitation_url_dialog":
            case "agent_needs_input":
              hs.AskingQuestion = true;
              break;
            case "elicitation_complete":
            case "elicitation_response":
              hs.AskingQuestion = false;
              break;
            case "quota_auto_resume_fired":
              hs.LimitHit = false;
              break;
          }
          break;
        case "Stop":
          Progress(sessionId).OnStop();
          hs.Completed = true;
          hs.AskedPermission = hs.PlanShown = hs.Compacting = false;
          hs.Activity = "";
          hs.Pending?.Answer.TrySetResult("");
          break;
        case "StopFailure":
          // The turn ended with an API error; a 429 means the plan limit is reached
          if (payload.ToJsonString().Contains("rate_limit", StringComparison.OrdinalIgnoreCase)) {
            hs.LimitHit = true;
          } else {
            hs.Failed = true;
          }
          break;
        case "PreCompact":
          hs.Compacting = true;
          break;
        case "PostCompact":
          hs.Compacting = false;
          break;
        case "SubagentStart":
          hs.Subagents++;
          hs.SubagentsStarted++;
          break;
        case "SubagentStop":
          hs.Subagents = Math.Max(0, hs.Subagents - 1);
          hs.SubagentsDone = Math.Min(hs.SubagentsStarted, hs.SubagentsDone + 1);
          break;
        case "SessionEnd":
          hs.Pending?.Answer.TrySetResult("");
          hs.Rewake?.TrySetResult("");
          _hookStates.Remove(sessionId);
          break;
        case "Rewake":
          // Turn over: wait for a keypad message; queued ones go right away
          hs.Rewake?.TrySetResult("");
          rewake = new TaskCompletionSource<string>(
              TaskCreationOptions.RunContinuationsAsynchronously);
          hs.Rewake = rewake;
          if (hs.Queued.Count > 0) {
            rewake.TrySetResult(string.Join("\n", hs.Queued));
            hs.Queued.Clear();
          }
          break;
        case "PermissionRequest" when GetString(payload, "tool_name") == QuestionTool:
          // A question needs a real answer in the editor; Allow would send it back unanswered
          hs.AskingQuestion = true;
          break;
        case "PermissionRequest" when GetString(payload, "tool_name") == PlanTool:
          // Approving a plan means picking how to go on (auto mode, approve edits, keep planning)
          hs.PlanShown = true;
          break;
        case "PermissionRequest":
          hs.Pending?.Answer.TrySetResult("");
          pending = new PendingPermission {
            ToolName = GetString(payload, "tool_name"),
            Summary = Summarize(payload["tool_input"]),
            Suggestions = payload["permission_suggestions"] as JsonArray
          };
          hs.Pending = pending;
          break;
      }

      // Messages sent from the keypad while Claude works reach it at the next tool result, or as
      // it stops (which then makes it continue). Only the main agent gets them: subagents
      // (agent_id set) report their tools with the same session id, but their replies never reach
      // the chat.
      if (hs.Queued.Count > 0 && eventName is "PostToolUse" or "PostToolUseFailure" or "Stop" &&
          string.IsNullOrEmpty(GetString(payload, "agent_id"))) {
        injected = KeypadNote(hs.Queued);
        hs.Queued.Clear();
        if (eventName == "Stop") {
          hs.Completed = false;
        }
      }
    }

    Scan(forceNotify: false);
    if (rewake != null) {
      // Stays open until a keypad message arrives, the session moves on, or Claude Code ends the
      // hook
      var gone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      using (hookGone.Register(() => gone.TrySetResult(true))) {
        await Task.WhenAny(rewake.Task, gone.Task);
      }
      lock (_lock) {
        if (!rewake.Task.IsCompleted) {
          // The hook went away first: nothing can wake Claude until its next turn ends
          rewake.TrySetResult("");
          if (_hookStates.TryGetValue(sessionId, out var hs) && hs.Rewake == rewake) {
            hs.Rewake = null;
          }
          PluginLog.Info($"[Phrase] Wake-up hook of {sessionId} ended");
          return "";
        }
        if (gone.Task.IsCompleted && rewake.Task.Result.Length > 0 &&
            _hookStates.TryGetValue(sessionId, out var state)) {
          // Both happened: keep the message for the next chance
          state.Queued.Add(rewake.Task.Result);
          return "";
        }
      }
      var message = rewake.Task.Result;
      if (message.Length > 0) {
        PluginLog.Info($"[Phrase] Woke {sessionId}");
        return KeypadNote(new[] { message });
      }
      return "";
    }
    if (injected != null) {
      PluginLog.Info($"[Phrase] Delivered to {sessionId} at {eventName}");
      return new JsonObject {
        ["hookSpecificOutput"] = new JsonObject {
          ["hookEventName"] = eventName,
          ["additionalContext"] = injected
        }
      }.ToJsonString();
    }
    if (pending == null) {
      return "";
    }

    // Hold the request until a key answers it, Claude Code's own dialog answers it, or time runs
    // out
    PluginLog.Info($"[Permission] {pending.ToolName} waiting in {sessionId}");
    var answered = await Task.WhenAny(
        pending.Answer.Task,
        Task.Delay(TimeSpan.FromSeconds(SetupService.PermissionHoldSeconds)));
    var answer = answered == pending.Answer.Task ? pending.Answer.Task.Result : "";
    lock (_lock) {
      if (_hookStates.TryGetValue(sessionId, out var hs) && hs.Pending == pending) {
        hs.Pending = null;
        // No key answered: Claude Code shows its dialog, the tile keeps asking for attention
        hs.AskedPermission = answer.Length == 0;
      }
    }
    Scan(forceNotify: false);
    return answer;
  }

  // Takes the context usage and the plan limits from a status line update
  private void OnStatusLine(string sessionId, JsonObject payload) {
    lock (_lock) {
      if (!string.IsNullOrEmpty(sessionId)) {
        if (!_hookStates.TryGetValue(sessionId, out var hs)) {
          hs = new HookState();
          _hookStates[sessionId] = hs;
        }
        var window = payload["context_window"] as JsonObject;
        var usage = window?["current_usage"] as JsonObject;
        if (usage != null) {
          hs.StatusLineContextTokens = GetInt64(usage, "input_tokens") +
              GetInt64(usage, "cache_creation_input_tokens") +
              GetInt64(usage, "cache_read_input_tokens");
        }
        var size = GetInt64(window, "context_window_size");
        if (size > 0) {
          hs.StatusLineContextMax = size;
        }
      }

      if (payload["rate_limits"] is JsonObject limits) {
        var quota = new PlanLimits { IsLive = true };
        quota.FiveHour = ParseLimit(limits["five_hour"] as JsonObject);
        quota.SevenDay = ParseLimit(limits["seven_day"] as JsonObject);
        _quota = quota;
      }
    }
    Scan(forceNotify: false);
  }

  private static LimitWindow ParseLimit(JsonObject window) {
    if (window == null) {
      return new LimitWindow();
    }

    var resets = GetInt64(window, "resets_at");
    return new LimitWindow {
      UsedPercent =
          window["used_percentage"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0,
      ResetsAtUtc = resets > 0 ? DateTimeOffset.FromUnixTimeSeconds(resets).UtcDateTime : null
    };
  }

  // Short description of what Claude wants to do, e.g. the command or the file name
  private static string Summarize(JsonNode input) {
    if (input is not JsonObject o) {
      return "";
    }

    foreach (var key in new[] { "command", "file_path", "path", "url", "pattern", "description" }) {
      var value = GetString(o, key);
      if (!string.IsNullOrEmpty(value)) {
        return key.Contains("path") ? Path.GetFileName(value) : value;
      }
    }
    return "";
  }

  // ---------------------------------------------------------------- Scan

  private void OnPollTick(object state) {
    if (Interlocked.CompareExchange(ref _isPolling, 1, 0) != 0) {
      return;
    }
    try {
      Scan(forceNotify: false);
    } catch (Exception ex) {
      PluginLog.Warning($"[Scan] {ex.Message}");
    } finally {
      Interlocked.Exchange(ref _isPolling, 0);
    }
  }

  // Rebuilds the session list from the session files, hook states and transcripts; raises
  // OnSessionsChanged when something shown changed
  private void Scan(bool forceNotify) {
    var probe = QuotaProbeService.RunningPid;
    var scanTime = DateTime.UtcNow;
    var files = ReadSessionFiles()
        .Where(f => f.Pid != probe && IsAlive(f.Pid))
        .Where(f => IsShown(f, scanTime))
        .ToList();

    string signature;
    bool changed;
    lock (_lock) {
      var now = DateTime.UtcNow;
      var previous = _sessions.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

      // Projects with several sessions get a number, so their tiles can be told apart
      var folderCounts = files.GroupBy(f => FolderName(f.Cwd), StringComparer.OrdinalIgnoreCase)
          .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
      var folderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

      var sessions = new List<ClaudeSession>();
      foreach (var f in files.OrderBy(f => f.Pid)) {
        _hookStates.TryGetValue(f.SessionId, out var hs);
        var folder = FolderName(f.Cwd);
        var name = !f.NameIsDerived && !string.IsNullOrWhiteSpace(f.Name) ? f.Name : folder;
        if (f.NameIsDerived && folderCounts.GetValueOrDefault(folder) > 1) {
          folderIndex[folder] = folderIndex.GetValueOrDefault(folder) + 1;
          name = $"{folder} #{folderIndex[folder]}";
        }

        var t = UpdateTranscript(f, hs);
        var state = ResolveState(f, hs, t);
        var session = new ClaudeSession {
          Id = f.SessionId,
          Pid = f.Pid,
          DisplayName = name,
          Cwd = f.Cwd,
          State = state,
          Activity = hs?.Activity ?? "",
          AgentCount = hs?.Subagents ?? 0,
          AgentsStarted = hs?.SubagentsStarted ?? 0,
          AgentsDone = hs?.SubagentsDone ?? 0,
          QueuedMessages = hs?.Queued.Count ?? 0,
          TasksDone = Progress(f.SessionId).Done,
          TasksTotal = Progress(f.SessionId).Total,
          CurrentTask = Progress(f.SessionId).CurrentTask,
          Remaining = Progress(f.SessionId).Remaining(now),
          Pending = hs?.Pending,
          LastUpdated =
              hs != null && hs.LastEventUtc > f.UpdatedUtc ? hs.LastEventUtc : f.UpdatedUtc,
          StatusSince = previous.TryGetValue(f.SessionId, out var old) && old.State == state ?
              old.StatusSince :
              now
        };

        session.TranscriptPath = t?.Path ?? "";
        session.Model = t?.Model ?? "";
        session.Effort = hs?.Effort ?? "";

        // A running workflow: its phase, agents and time left (agent counts from its journal are
        // exact)
        var wf = Workflow(f.SessionId, session.TranscriptPath, now);
        if (wf != null) {
          session.WorkflowPhase = wf.Phase;
          session.WorkflowPhaseIndex = wf.PhaseIndex;
          session.WorkflowPhaseCount = wf.PhaseCount;
          session.AgentsStarted = wf.AgentsStarted;
          session.AgentsDone = wf.AgentsDone;
          session.Remaining ??= wf.Remaining;
        }
        session.InputTokens = (t?.Input ?? 0) + (t?.CacheCreation ?? 0);
        session.OutputTokens = t?.Output ?? 0;
        session.CacheReadTokens = t?.CacheRead ?? 0;
        session.ContextUsedTokens = hs?.StatusLineContextTokens > 0 ?
            hs.StatusLineContextTokens :
            t?.ContextUsed ?? 0;
        session.ContextMaxTokens = hs?.StatusLineContextMax ?? 0;
        sessions.Add(session);
      }

      _board.Update(sessions, _heldKey);
      var bySlot = new Dictionary<int, ClaudeSession>();
      foreach (var s in sessions) {
        var slot = _board.SlotOf(s.SlotKey);
        if (slot.HasValue) {
          bySlot[slot.Value] = s;
        }
      }

      _sessions = sessions;
      _sessionsBySlot = bySlot;

      // Forget hook state of sessions that are gone
      var ids = sessions.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
      var goneIds = _hookStates.Keys
          .Where(id => !ids.Contains(id) &&
              now - _hookStates[id].LastEventUtc > TimeSpan.FromMinutes(5))
          .ToList();
      foreach (var gone in goneIds) {
        _hookStates.Remove(gone);
        _transcripts.Remove(gone);
        _progress.Remove(gone);
        _workflows.Remove(gone);
      }

      var sb = new StringBuilder();
      foreach (var kv in bySlot.OrderBy(k => k.Key)) {
        var s = kv.Value;
        sb.Append(kv.Key).Append('|').Append(s.Id).Append('|').Append(s.DisplayName)
            .Append('|').Append(s.State)
            .Append('|').Append(s.TotalContextTokens / 1000).Append('|').Append(s.Activity)
            .Append('|').Append(s.StatusPillText).Append('|').Append(s.CurrentTask)
            .Append('|').Append(s.QueuedMessages).Append('|').Append(s.ModelLabel).Append(';');
      }
      sb.Append(_quota.IsLive)
          .Append(_quota.FiveHour.UsedPercent)
          .Append(_quota.SevenDay.UsedPercent);
      signature = sb.ToString();
      changed = signature != _lastChangeSignature || forceNotify;
      _lastChangeSignature = signature;
    }

    if (changed) {
      OnSessionsChanged?.Invoke();
    }
  }

  // An idle extension session that no editor tab shows (replaced by one opened from the history)
  // keeps its process for a while: no tile for it. Busy ones stay, and so do terminal sessions.
  private static bool IsShown(SessionFile f, DateTime now) {
    if (f.Entrypoint != "claude-vscode" ||
        string.Equals(f.Status, "busy", StringComparison.OrdinalIgnoreCase)) {
      return true;
    }
    try {
      return EditorTabs.IsOpen(f.SessionId, f.Cwd, f.StartedUtc, now);
    } catch (Exception) {
      return true;
    }
  }

  // Most urgent first: what needs the user, then what blocks Claude, then what it is doing
  private static AgentSessionState ResolveState(SessionFile f, HookState hs, TranscriptState t) {
    // A missed SubagentStop or PostCompact (editor killed, hook timed out) must not leave the tile
    // stuck: once the session is idle and quiet for a while, those counters are reset
    if (hs != null && !string.Equals(f.Status, "busy", StringComparison.OrdinalIgnoreCase) &&
        DateTime.UtcNow - hs.LastEventUtc > StaleHookState) {
      hs.Subagents = 0;
      hs.Compacting = false;
    }

    if (hs?.Pending != null) {
      return AgentSessionState.Permission;
    }

    if (hs?.AskingQuestion == true) {
      return AgentSessionState.Question;
    }

    if (hs?.PlanShown == true) {
      return AgentSessionState.PlanReady;
    }

    if (hs?.AskedPermission == true) {
      return AgentSessionState.Permission;
    }

    if (hs?.LimitHit == true || t?.LimitHit == true) {
      return AgentSessionState.LimitHit;
    }

    var busy = string.Equals(f.Status, "busy", StringComparison.OrdinalIgnoreCase);
    if (busy) {
      if (hs?.Compacting == true) {
        return AgentSessionState.Compacting;
      }

      return hs?.Subagents > 0 ? AgentSessionState.Agents : AgentSessionState.Thinking;
    }
    if (hs?.Failed == true) {
      return AgentSessionState.Error;
    }

    // Background subagents keep working after the main turn ended
    if (hs?.Subagents > 0) {
      return AgentSessionState.Agents;
    }

    if (hs?.Completed == true) {
      return AgentSessionState.Complete;
    }

    return AgentSessionState.Idle;
  }

  private bool IsAlive(int pid) {
    lock (_lock) {
      if (_liveness.TryGetValue(pid, out var known) &&
          DateTime.UtcNow - known.CheckedAt < LivenessInterval) {
        return known.Alive;
      }
    }
    var alive = WindowsPlatform.IsAlive(pid);
    lock (_lock) {
      _liveness[pid] = (alive, DateTime.UtcNow);
    }
    return alive;
  }

  private static List<SessionFile> ReadSessionFiles() {
    var result = new List<SessionFile>();
    if (!Directory.Exists(SessionsDir)) {
      return result;
    }

    foreach (var path in Directory.EnumerateFiles(SessionsDir, "*.json")) {
      try {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (JsonNode.Parse(stream) is not JsonObject o) {
          continue;
        }

        var file = new SessionFile {
          Pid = (int)GetInt64(o, "pid"),
          SessionId = GetString(o, "sessionId"),
          Cwd = GetString(o, "cwd"),
          Name = GetString(o, "name"),
          NameIsDerived = GetString(o, "nameSource") is "" or "derived",
          Status = GetString(o, "status"),
          Kind = GetString(o, "kind"),
          Entrypoint = GetString(o, "entrypoint"),
          StartedUtc = DateTimeOffset.FromUnixTimeMilliseconds(
              Math.Max(0, GetInt64(o, "startedAt"))).UtcDateTime,
          UpdatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(
              Math.Max(0, GetInt64(o, "updatedAt"))).UtcDateTime
        };
        // Only sessions people work in: not "claude -p" runs such as the quota probe
        if (file.Pid > 0 && !string.IsNullOrEmpty(file.SessionId) &&
            file.Kind is "" or "interactive") {
          result.Add(file);
        }
      } catch (Exception ex) when (
          ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) {
        // Being rewritten right now; read again on the next tick
      }
    }
    return result;
  }

  // ---------------------------------------------------------------- Transcripts

  // Reads new transcript lines: token totals of distinct assistant messages and the context of the
  // last one
  private TranscriptState UpdateTranscript(SessionFile f, HookState hs) {
    if (!_transcripts.TryGetValue(f.SessionId, out var t)) {
      t = new TranscriptState();
      _transcripts[f.SessionId] = t;
    }

    var now = DateTime.UtcNow;
    if (now - t.ReadAt < TranscriptInterval) {
      return t;
    }
    t.ReadAt = now;

    var path = !string.IsNullOrEmpty(hs?.TranscriptPath) ? hs.TranscriptPath : t.Path;
    if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
      path = FindTranscript(f.SessionId);
    }
    if (string.IsNullOrEmpty(path)) {
      return t;
    }
    if (!string.Equals(path, t.Path, StringComparison.OrdinalIgnoreCase)) {
      t.Path = path;
      t.Offset = 0;
      t.SeenMessages.Clear();
      t.Input = t.Output = t.CacheRead = t.CacheCreation = t.ContextUsed = 0;
    }

    try {
      using var stream = new FileStream(
          path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
      if (stream.Length < t.Offset) {
        t.Offset = 0;
        t.SeenMessages.Clear();
        t.Input = t.Output = t.CacheRead = t.CacheCreation = t.ContextUsed = 0;
      }
      if (stream.Length == t.Offset) {
        return t;
      }

      stream.Seek(t.Offset, SeekOrigin.Begin);
      var buffer = new byte[stream.Length - t.Offset];
      var read = stream.Read(buffer, 0, buffer.Length);
      if (read <= 0) {
        return t;
      }

      // Only complete lines; a line still being written is read next time
      var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
      if (lastNewline < 0) {
        return t;
      }
      t.Offset += lastNewline + 1;

      foreach (var line in Encoding.UTF8.GetString(buffer, 0, lastNewline).Split('\n')) {
        var isRateLimit = line.Contains("\"error\":\"rate_limit\"");
        if (!isRateLimit && !line.Contains("\"usage\"")) {
          continue;
        }

        try {
          if (JsonNode.Parse(line) is not JsonObject entry ||
              GetString(entry, "type") != "assistant") {
            continue;
          }

          t.LimitHit = GetString(entry, "error") == "rate_limit";
          if (line.Contains("[ETA") &&
              (entry["message"] as JsonObject)?["content"] is JsonArray content &&
              DateTime.TryParse(
                  GetString(entry, "timestamp"), CultureInfo.InvariantCulture,
                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                  out var written)) {
            var textBlocks =
                content.OfType<JsonObject>().Where(b => GetString(b, "type") == "text");
            foreach (var block in textBlocks) {
              Progress(f.SessionId).OnAssistantText(GetString(block, "text"), written);
            }
          }
          if (entry["message"] is not JsonObject message ||
              message["usage"] is not JsonObject usage) {
            continue;
          }

          var model = GetString(message, "model");
          if (model.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) {
            t.Model = model;
          }

          var input = GetInt64(usage, "input_tokens");
          var cacheCreation = GetInt64(usage, "cache_creation_input_tokens");
          var cacheRead = GetInt64(usage, "cache_read_input_tokens");
          t.ContextUsed = input + cacheCreation + cacheRead;

          // One assistant message is written as several lines that repeat its usage
          var id = GetString(message, "id");
          if (id.Length > 0 && !t.SeenMessages.Add(id)) {
            continue;
          }

          t.Input += input;
          t.CacheCreation += cacheCreation;
          t.CacheRead += cacheRead;
          t.Output += GetInt64(usage, "output_tokens");
        } catch (JsonException) {
        }
      }
    } catch (IOException) {
    }
    return t;
  }

  private static string FindTranscript(string sessionId) {
    if (!Directory.Exists(ProjectsDir)) {
      return "";
    }

    try {
      return Directory
          .EnumerateFiles(ProjectsDir, sessionId + ".jsonl", SearchOption.AllDirectories)
          .FirstOrDefault() ?? "";
    } catch (Exception) {
      return "";
    }
  }

  // ---------------------------------------------------------------- Helpers

  // Call under _lock; the workflow files are read at most every few seconds
  private WorkflowTracker.Progress Workflow(string sessionId, string transcriptPath, DateTime now) {
    if (_workflows.TryGetValue(sessionId, out var cached) &&
        now - cached.ReadAt < WorkflowInterval) {
      return cached.Progress;
    }
    WorkflowTracker.Progress progress = null;
    try {
      progress = WorkflowTracker.Read(transcriptPath, sessionId, now);
    } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
    }
    _workflows[sessionId] = (now, progress);
    return progress;
  }

  // Call under _lock
  private ProgressTracker Progress(string sessionId) {
    if (!_progress.TryGetValue(sessionId, out var p)) {
      p = new ProgressTracker();
      _progress[sessionId] = p;
    }
    return p;
  }

  private static string FolderName(string cwd) {
    var name = Path.GetFileName((cwd ?? "").TrimEnd('\\', '/'));
    return string.IsNullOrEmpty(name) ? "Claude" : name;
  }

  private static string GetString(JsonObject o, string key) =>
      o != null && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

  private static long GetInt64(JsonObject o, string key) {
    if (o == null || o[key] is not JsonValue v) {
      return 0;
    }

    if (v.TryGetValue<long>(out var n)) {
      return n;
    }

    if (v.TryGetValue<double>(out var d)) {
      return (long)d;
    }

    return v.TryGetValue<string>(out var s) && long.TryParse(s, out var p) ? p : 0;
  }

  public void Dispose() {
    _pollTimer.Dispose();
    _quotaProbe.Dispose();
    _hookServer.Dispose();
  }
}
