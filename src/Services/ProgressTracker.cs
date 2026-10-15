using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Loupedeck.ClaudeCodePlugin.Services;

// How far a session is and how long it may still take, from two sources:
//  - Claude's task list (TodoWrite, or TaskCreate/TaskUpdate): tasks done out of total, and the
//    time left at the pace so far (time per finished task x tasks left), counting down between
//    updates;
//  - Claude's own estimate, when asked: an "[ETA: 12 min | Step: tests]" marker in its answer.
public sealed class ProgressTracker {
  private sealed class TaskItem {
    public string Id = "";
    public string Subject = "";
    public string ActiveForm = "";
    public string Status = "pending";
  }

  private static readonly Regex EtaMarker = new(
      @"\[ETA\s*:\s*(\d+)\s*min(?:[^|\]]*\|\s*(?:Step|[ÉEée]tape)\s*:\s*([^\]]+))?\]",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

  private readonly List<TaskItem> _tasks = new();
  private int _lastTaskId;
  private DateTime? _startedUtc;
  private DateTime? _lastCompletionUtc;
  private int _done;

  private static readonly TimeSpan StaleEstimate = TimeSpan.FromHours(2);
  private DateTime? _etaEndUtc;
  private string _etaStep = "";
  private DateTime _lastStopUtc = DateTime.MinValue;

  public int Done => _done;
  public int Total => _tasks.Count;

  // Task being worked on (its "doing" form when Claude gave one), else the step of Claude's
  // estimate.
  public string CurrentTask {
    get {
      var task = _tasks.FirstOrDefault(t => t.Status == "in_progress");
      if (task != null) {
        return string.IsNullOrEmpty(task.ActiveForm) ? task.Subject : task.ActiveForm;
      }
      return _etaStep;
    }
  }

  // Time left (negative once the estimate is exceeded), or null when unknown. The pace of the
  // task list wins once a task is done; Claude's estimate is the fallback.
  public TimeSpan? Remaining(DateTime now) {
    if (_done >= 1 && Total > _done && _startedUtc.HasValue && _lastCompletionUtc.HasValue) {
      var perTask = (_lastCompletionUtc.Value - _startedUtc.Value) / _done;
      return perTask * (Total - _done) - (now - _lastCompletionUtc.Value);
    }
    return _etaEndUtc.HasValue ? _etaEndUtc.Value - now : null;
  }

  // ---------------------------------------------------------------- Task list

  // Updates the task list from a TodoWrite, TaskCreate or TaskUpdate tool call.
  public void OnTool(string toolName, JsonObject input, DateTime now) {
    if (input == null) {
      return;
    }

    switch (toolName) {
      case "TodoWrite":
        OnTodoWrite(input["todos"] as JsonArray, now);
        break;
      case "TaskCreate":
        // A new list starts once the previous one is finished.
        if (_tasks.Count > 0 && _tasks.All(t => t.Status == "completed")) {
          ResetTasks();
        }
        _startedUtc ??= now;
        _tasks.Add(new TaskItem {
          Id = (++_lastTaskId).ToString(),
          Subject = Str(input, "subject"),
          ActiveForm = Str(input, "activeForm")
        });
        break;
      case "TaskUpdate":
        var id = Str(input, "taskId");
        var task = _tasks.FirstOrDefault(t => t.Id == id);
        if (task == null) {
          task = new TaskItem { Id = id, Subject = Str(input, "subject") };
          _tasks.Add(task);
          _startedUtc ??= now;
        }
        if (Str(input, "subject").Length > 0) {
          task.Subject = Str(input, "subject");
        }

        if (Str(input, "activeForm").Length > 0) {
          task.ActiveForm = Str(input, "activeForm");
        }

        var status = Str(input, "status");
        if (status == "deleted") {
          if (task.Status == "completed") {
            _done--;
          }

          _tasks.Remove(task);
        } else if (status.Length > 0) {
          SetStatus(task, status, now);
        }
        break;
    }
  }

  // TodoWrite sends the whole list each time.
  private void OnTodoWrite(JsonArray todos, DateTime now) {
    if (todos == null) {
      return;
    }

    var items = todos.OfType<JsonObject>().Select(t => new TaskItem {
      Subject = Str(t, "content"),
      ActiveForm = Str(t, "activeForm"),
      Status = Str(t, "status")
    }).ToList();

    // A list with nothing done yet that differs from the current one is a new list.
    var newList = items.All(t => t.Status != "completed") &&
        !items.Select(t => t.Subject).SequenceEqual(_tasks.Select(t => t.Subject));
    var doneBefore = newList ? 0 : _done;
    if (newList) {
      ResetTasks();
      _startedUtc = now;
    }
    _startedUtc ??= now;

    _tasks.Clear();
    for (var i = 0; i < items.Count; i++) {
      items[i].Id = (i + 1).ToString();
      _tasks.Add(items[i]);
    }
    _done = items.Count(t => t.Status == "completed");
    if (_done > doneBefore) {
      _lastCompletionUtc = now;
    }
  }

  private void SetStatus(TaskItem task, string status, DateTime now) {
    if (status == task.Status) {
      return;
    }

    if (status == "completed") {
      _done++;
      _lastCompletionUtc = now;
    } else if (task.Status == "completed") {
      _done--;
    }
    task.Status = status;
  }

  private void ResetTasks() {
    _tasks.Clear();
    _done = 0;
    _startedUtc = null;
    _lastCompletionUtc = null;
  }

  // ---------------------------------------------------------------- Claude's estimate

  // Looks for an "[ETA: N min | Step: ...]" marker in Claude's text written at the given time.
  public void OnAssistantText(string text, DateTime writtenUtc) {
    // Transcripts are read a little late and from the start: skip estimates from finished turns
    // and old ones.
    if (writtenUtc <= _lastStopUtc || DateTime.UtcNow - writtenUtc > StaleEstimate) {
      return;
    }

    var match = EtaMarker.Match(text ?? "");
    if (!match.Success) {
      return;
    }

    _etaEndUtc = writtenUtc + TimeSpan.FromMinutes(int.Parse(match.Groups[1].Value));
    _etaStep = match.Groups[2].Success ? match.Groups[2].Value.Trim() : "";
  }

  // Called when the turn ends: its estimate does not apply to the next one.
  public void OnStop() {
    _lastStopUtc = DateTime.UtcNow;
    _etaEndUtc = null;
    _etaStep = "";
  }

  // A value as a string, whatever its JSON type ("" when missing).
  private static string Str(JsonObject o, string key) =>
      o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[key]?.ToString() ?? "";
}
