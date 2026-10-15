using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AgentPad.Shared;

namespace ClaudeKeypadHook;

// Claude Code hook and status line command of AgentPad for IDEs (MX Keypad plugin).
//
//   claude-keypad-hook <HookEventName>  hook: forwards the event JSON from stdin to the plugin
//   claude-keypad-hook statusline       status line: forwards the JSON, prints nothing
//   claude-keypad-hook --uninstall      removes AgentPad's hooks from ~/.claude/settings.json
//   claude-keypad-hook Rewake           asyncRewake hook run when a turn ends: waits in the
//                                       background for a keypad message for this session;
//                                       prints it on stderr and exits with 2, which wakes Claude
//                                       with it, or exits with 0 when there is none
//
// PermissionRequest waits for an answer (the plugin holds it until a key is pressed or it gives
// up); PostToolUse, PostToolUseFailure and Stop get keypad messages for Claude, if any. Answers
// are printed as the hook output. Whenever the plugin is not running, the hook exits at once with
// exit code 0 and no output, so Claude Code carries on as if there were no hook.
internal static class Program {
  // Must match HookServer.PipeName in the plugin.
  private const string PipeName = "logi-claude-code-keypad";
  private const int ConnectTimeoutMs = 250;

  // Events whose output Claude Code uses: permission answers, and keypad messages for Claude.
  private static readonly string[] AnsweredEvents = {
    "PermissionRequest", "PostToolUse", "PostToolUseFailure", "Stop", "Rewake"
  };
  private const int MaxMessageBytes = 4 * 1024 * 1024;

  private static int Main(string[] args) {
    if (args.Length > 0 && args[0] == "--uninstall") {
      return Uninstall();
    }

    try {
      var eventName = args.Length > 0 ? args[0] : "";
      var input = ReadStdin();
      if (string.IsNullOrEmpty(eventName) || string.IsNullOrWhiteSpace(input)) {
        return 0;
      }

      var answer = Send(
          eventName, input, waitForAnswer: Array.IndexOf(AnsweredEvents, eventName) >= 0);
      if (eventName == "Rewake") {
        if (string.IsNullOrEmpty(answer)) {
          return 0;
        }
        var stderr = Console.OpenStandardError();
        var message = new UTF8Encoding(false).GetBytes(answer);
        stderr.Write(message, 0, message.Length);
        stderr.Flush();
        return 2;
      }
      if (!string.IsNullOrEmpty(answer)) {
        var stdout = Console.OpenStandardOutput();
        var bytes = new UTF8Encoding(false).GetBytes(answer);
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
      }
    } catch {
      // Never get in Claude Code's way.
    }
    return 0;
  }

  // Removes AgentPad's hooks and status line from ~/.claude/settings.json and tells the plugin
  // (if it is still installed) not to put them back. For use after uninstalling the plugin.
  private static int Uninstall() {
    try {
      var path = HookSettings.ClaudeSettingsPath;
      if (HookSettings.Read(path, "{}", out var original) is JsonObject root &&
          HookSettings.RemoveOurs(root)) {
        HookSettings.WriteIfChanged(path, original, root);
        Console.WriteLine($"AgentPad hooks removed from {path}");
      } else {
        Console.WriteLine("No AgentPad hooks found");
      }
      Directory.CreateDirectory(HookSettings.DataDir);
      File.WriteAllText(HookSettings.RemovedMarker, DateTime.UtcNow.ToString("o"));
      return 0;
    } catch (Exception ex) {
      Console.Error.WriteLine($"Could not remove the hooks: {ex.Message}");
      return 1;
    }
  }

  // Reads stdin as UTF-8, giving up after 5 seconds.
  private static string ReadStdin() {
    using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    var read = reader.ReadToEndAsync();
    return Task.WaitAny(new Task[] { read }, 5000) == 0 ? read.Result : "";
  }

  // Sends the event to the plugin and returns its answer ("" for none).
  // Message: [4-byte length][UTF-8 "event\n" + json]; answer: [4-byte length][UTF-8 hook output].
  private static string Send(string eventName, string json, bool waitForAnswer) {
    using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
    try {
      pipe.Connect(ConnectTimeoutMs);
    } catch (TimeoutException) {
      return "";
    }

    var payload = Encoding.UTF8.GetBytes(eventName + "\n" + json);
    pipe.Write(BitConverter.GetBytes(payload.Length), 0, 4);
    pipe.Write(payload, 0, payload.Length);
    pipe.Flush();

    if (!waitForAnswer) {
      return "";
    }

    var header = ReadExactly(pipe, 4);
    if (header == null) {
      return "";
    }
    var length = BitConverter.ToInt32(header, 0);
    if (length <= 0 || length > MaxMessageBytes) {
      return "";
    }
    var body = ReadExactly(pipe, length);
    return body == null ? "" : Encoding.UTF8.GetString(body);
  }

  // Reads exactly count bytes, or returns null when the stream ends first.
  private static byte[] ReadExactly(Stream stream, int count) {
    var buffer = new byte[count];
    var offset = 0;
    while (offset < count) {
      var n = stream.Read(buffer, offset, count - offset);
      if (n == 0) {
        return null;
      }
      offset += n;
    }
    return buffer;
  }
}
