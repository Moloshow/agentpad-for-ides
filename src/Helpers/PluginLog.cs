using System;

namespace Loupedeck.ClaudeCodePlugin;

// Writes to the plugin's log file (Logs\plugin_logs\ClaudeCodeSessions.log) once the plugin hands
// its logger over; messages before that are dropped
internal static class PluginLog {
  private static PluginLogFile _log;

  public static void Init(PluginLogFile log) => _log = log;

  public static void Info(string message) => _log?.Info(message);

  public static void Warning(string message) => _log?.Warning(message);

  public static void Error(string message) => _log?.Error(message);
}
