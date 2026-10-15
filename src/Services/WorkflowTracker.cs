using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Loupedeck.ClaudeCodePlugin.Services;

// Progress of a workflow a session runs, from the files Claude Code keeps next to the transcript:
//   <session>/workflows/scripts/<name>-<runId>.js        the script, whose meta lists the phases
//   <session>/subagents/workflows/<runId>/journal.jsonl  agents started and finished, with phase
//   <session>/subagents/workflows/<runId>/agent-*.meta.json  written when an agent starts
//   <session>/workflows/<runId>.json                     written when the workflow ends
// Time left = average length of the finished phases x phases left, minus the time spent in the
// current one.
public static class WorkflowTracker {
  // Snapshot of a running workflow.
  public sealed class Progress {
    public string Name = "";
    public string Phase = "";
    public int PhaseIndex;  // 1-based
    public int PhaseCount;
    public int AgentsStarted;
    public int AgentsDone;
    public TimeSpan? Remaining;
  }

  private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
  private static readonly Regex PhasesBlock =
      new(@"phases\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
  private static readonly Regex PhaseTitle = new(@"title\s*:\s*(['""`])(.*?)\1");
  private static readonly Regex MetaName = new(@"name\s*:\s*(['""`])(.*?)\1");

  // The running workflow of a session, or null.
  public static Progress Read(string transcriptPath, string sessionId, DateTime now) {
    if (string.IsNullOrEmpty(transcriptPath)) {
      return null;
    }

    var sessionDir = Path.Combine(Path.GetDirectoryName(transcriptPath) ?? "", sessionId);
    var runsDir = Path.Combine(sessionDir, "subagents", "workflows");
    if (!Directory.Exists(runsDir)) {
      return null;
    }

    // Most recent run that has not written its end state yet.
    var run = new DirectoryInfo(runsDir).EnumerateDirectories("wf_*")
        .Where(d => File.Exists(Path.Combine(d.FullName, "journal.jsonl")))
        .Where(d => !File.Exists(Path.Combine(sessionDir, "workflows", d.Name + ".json")))
        .Where(d =>
            now - File.GetLastWriteTimeUtc(Path.Combine(d.FullName, "journal.jsonl")) < MaxAge)
        .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d.FullName, "journal.jsonl")))
        .FirstOrDefault();
    if (run == null) {
      return null;
    }

    var progress = new Progress();
    var phases = new List<string>();
    var scriptsDir = Path.Combine(sessionDir, "workflows", "scripts");
    var script = Directory.Exists(scriptsDir)
        ? Directory.EnumerateFiles(scriptsDir, "*" + run.Name + ".js").FirstOrDefault()
        : null;
    if (script != null) {
      var text = File.ReadAllText(script);
      var block = PhasesBlock.Match(text);
      if (block.Success) {
        phases = PhaseTitle.Matches(block.Groups[1].Value).Select(m => m.Groups[2].Value).ToList();
      }
      var name = MetaName.Match(text);
      progress.Name = name.Success ? name.Groups[2].Value : "";
    }

    // Journal: which agents started (and in which phase) and which finished. Shared read access,
    // since Claude Code keeps appending to it.
    var started = new List<(string Agent, string Phase)>();
    var finished = new HashSet<string>();
    using (var stream = new FileStream(
        Path.Combine(run.FullName, "journal.jsonl"), FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete))
    using (var reader = new StreamReader(stream)) {
      string line;
      while ((line = reader.ReadLine()) != null) {
        try {
          if (JsonNode.Parse(line) is not JsonObject entry) {
            continue;
          }

          var type = entry["type"]?.GetValue<string>();
          var agent = entry["agentId"]?.GetValue<string>() ?? "";
          if (type == "started") {
            started.Add((agent, entry["phase"]?.GetValue<string>() ?? ""));
          } else if (type == "result") {
            finished.Add(agent);
          }
        } catch (Exception) {
          // A line being written or of another shape: skipped.
        }
      }
    }
    progress.AgentsStarted = started.Count;
    progress.AgentsDone = started.Count(s => finished.Contains(s.Agent));

    // Current phase: the one of the latest agent still running, else of the latest agent.
    var current = started.LastOrDefault(s => !finished.Contains(s.Agent));
    if (current.Agent == null) {
      current = started.LastOrDefault();
    }

    progress.Phase = current.Phase ?? "";
    var index = phases.FindIndex(
        p => string.Equals(p, progress.Phase, StringComparison.OrdinalIgnoreCase));
    if (index < 0 || phases.Count == 0) {
      return progress;
    }
    progress.PhaseIndex = index + 1;
    progress.PhaseCount = phases.Count;

    // Pace of the finished phases: from the workflow start to the start of the current phase.
    if (index >= 1) {
      var launched = File.GetCreationTimeUtc(Path.Combine(run.FullName, "journal.jsonl"));
      var phaseStart = started
          .Where(s => s.Phase == progress.Phase)
          .Select(s => Path.Combine(run.FullName, $"agent-{s.Agent}.meta.json"))
          .Where(File.Exists)
          .Select(File.GetLastWriteTimeUtc)
          .DefaultIfEmpty(now)
          .Min();
      if (phaseStart > launched) {
        var perPhase = (phaseStart - launched) / index;
        progress.Remaining = perPhase * (phases.Count - index) - (now - phaseStart);
      }
    }
    return progress;
  }
}
