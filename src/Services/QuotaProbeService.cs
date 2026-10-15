using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Loupedeck.ClaudeCodePlugin.Models;

namespace Loupedeck.ClaudeCodePlugin.Services;

// Reads the plan limits (5-hour and weekly windows) the way the Claude Code extension gets them:
// from the rate_limit_event that Claude Code prints in stream-json mode. The editor extensions do
// not run the status line, so this is the only source there. Each probe is one tiny Haiku request
// (no tools, a one-line system prompt, no settings so no hooks, nothing saved): about 930 input
// and 4 output Haiku tokens.
public sealed class QuotaProbeService : IDisposable {
  private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
  private static readonly TimeSpan MinGap = TimeSpan.FromMinutes(1);
  private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

  // Folders of editor extensions that bundle the Claude Code binary.
  private static readonly string[] ExtensionDirs = {
    ".antigravity-ide", ".antigravity", ".vscode", ".vscode-insiders", ".cursor", ".windsurf"
  };

  private readonly Func<bool> _hasSessions;
  private readonly Action<PlanLimits> _onQuota;
  private readonly Timer _timer;
  private int _running;
  private DateTime _lastProbe = DateTime.MinValue;

  // Process of a probe in flight: it writes a session file like any session, which must not get a
  // tile.
  public static int RunningPid { get; private set; }

  // Probes every Interval while hasSessions() is true; onQuota receives each result.
  public QuotaProbeService(Func<bool> hasSessions, Action<PlanLimits> onQuota) {
    _hasSessions = hasSessions;
    _onQuota = onQuota;
    _timer = new Timer(_ => Probe(force: false), null, TimeSpan.FromSeconds(15), Interval);
  }

  // Starts a probe in the background. force: probe even without open sessions (still at most once
  // a minute, and never two at a time).
  public void Probe(bool force) {
    if (!force && !_hasSessions()) {
      return;
    }

    if (DateTime.UtcNow - _lastProbe < MinGap) {
      return;
    }

    if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) {
      return;
    }

    _lastProbe = DateTime.UtcNow;

    Task.Run(() => {
      try {
        var quota = RunProbe();
        if (quota != null) {
          PluginLog.Info(
              $"[Quota] 5h {quota.FiveHour.UsedPercent:0}%, 7d {quota.SevenDay.UsedPercent:0}%");
          _onQuota(quota);
        }
      } catch (Exception ex) {
        PluginLog.Warning($"[Quota] Probe failed: {ex.Message}");
      } finally {
        Interlocked.Exchange(ref _running, 0);
      }
    });
  }

  private static PlanLimits RunProbe() {
    var exe = FindClaude();
    if (exe == null) {
      PluginLog.Warning("[Quota] Claude Code binary not found");
      return null;
    }

    var psi = new ProcessStartInfo {
      FileName = exe,
      UseShellExecute = false,
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      CreateNoWindow = true,
      WorkingDirectory = Path.GetTempPath()
    };
    // Smallest request Claude Code makes with a subscription login (--bare needs an API key). No
    // tools, skills, MCP servers or settings, so only the fixed part of Claude Code's prompt is
    // sent.
    foreach (var arg in new[] {
      "-p", "Say OK", "--model", "haiku", "--effort", "low",
      "--output-format", "stream-json", "--verbose",
      "--no-session-persistence", "--setting-sources", "", "--max-turns", "1",
      "--system-prompt", "Reply with the single word OK.", "--tools", "",
      "--disable-slash-commands",
      "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--no-chrome"
    }) {
      psi.ArgumentList.Add(arg);
    }

    using var process = Process.Start(psi);
    if (process == null) {
      return null;
    }

    RunningPid = process.Id;
    try {
      return ReadProbe(process);
    } finally {
      RunningPid = 0;
    }
  }

  // Waits for the probe to finish and returns the limits of its last rate_limit_event, or null.
  private static PlanLimits ReadProbe(Process process) {
    process.StandardInput.Close();
    _ = process.StandardError.ReadToEndAsync();

    PlanLimits quota = null;
    var output = process.StandardOutput.ReadToEndAsync();
    if (!output.Wait(ProbeTimeout)) {
      try {
        process.Kill();
      } catch {
      }
      return null;
    }

    foreach (var line in output.Result.Split('\n')) {
      if (!line.Contains("rate_limit_event")) {
        continue;
      }

      try {
        var windows = (JsonNode.Parse(line) as JsonObject)?["rate_limit_info"]?["unifiedWindows"]
            as JsonObject;
        if (windows == null) {
          continue;
        }

        quota = new PlanLimits { IsLive = true };
        quota.FiveHour = ParseWindow(windows["five_hour"] as JsonObject);
        quota.SevenDay = ParseWindow(windows["seven_day"] as JsonObject);
      } catch (System.Text.Json.JsonException) {
      }
    }
    return quota;
  }

  // utilization is a fraction (0.34 = 34 %), resetsAt is in Unix seconds.
  private static LimitWindow ParseWindow(JsonObject window) {
    if (window == null) {
      return new LimitWindow();
    }

    var utilization =
        window["utilization"] is JsonValue u && u.TryGetValue<double>(out var d) ? d : 0;
    var resets = window["resetsAt"] is JsonValue r && r.TryGetValue<long>(out var s) ? s : 0;
    return new LimitWindow {
      UsedPercent = utilization * 100.0,
      ResetsAtUtc = resets > 0 ? DateTimeOffset.FromUnixTimeSeconds(resets).UtcDateTime : null
    };
  }

  // Newest Claude Code binary bundled with an editor extension, else the standalone install.
  private static string FindClaude() {
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var bundled = ExtensionDirs
        .Select(d => Path.Combine(home, d, "extensions"))
        .Where(Directory.Exists)
        .SelectMany(d => Directory.EnumerateDirectories(d, "anthropic.claude-code-*"))
        .Select(d => Path.Combine(d, "resources", "native-binary", "claude.exe"))
        .Where(File.Exists)
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();
    if (bundled != null) {
      return bundled;
    }

    var standalone = Path.Combine(home, ".local", "bin", "claude.exe");
    return File.Exists(standalone) ? standalone : null;
  }

  public void Dispose() => _timer.Dispose();
}
