using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Loupedeck.ClaudeCodePlugin.Services;

// Receives Claude Code hook events from claude-keypad-hook.exe over a named pipe.
// Message: [4-byte length][UTF-8 "event\n" + json]. Events listed in AnsweredEvents get an answer
// back in the same framing.
public sealed class HookServer : IDisposable {
  // Must match the hook executable.
  public const string PipeName = "logi-claude-code-keypad";

  // Events whose hook waits for an answer (must match the hook executable and SetupService).
  public static readonly string[] AnsweredEvents = {
    "PermissionRequest", "PostToolUse", "PostToolUseFailure", "Stop", "Rewake"
  };
  private const int MaxMessageBytes = 4 * 1024 * 1024;

  private readonly Func<string, JsonObject, CancellationToken, Task<string>> _handler;
  private readonly CancellationTokenSource _cts = new();

  // Starts listening. handler(eventName, payload, hookGone) returns the hook output ("" for none);
  // hookGone is canceled when the hook process goes away first (Claude Code canceled or killed
  // it).
  public HookServer(Func<string, JsonObject, CancellationToken, Task<string>> handler) {
    _handler = handler;
    _ = Task.Run(AcceptLoop);
  }

  private async Task AcceptLoop() {
    while (!_cts.IsCancellationRequested) {
      NamedPipeServerStream pipe = null;
      try {
        pipe = new NamedPipeServerStream(
            PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(_cts.Token);

        // Each client is served on its own, so a held permission request never blocks other
        // events.
        var client = pipe;
        pipe = null;
        _ = Task.Run(() => Serve(client));
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        PluginLog.Warning($"[HookServer] Accept failed: {ex.Message}");
        await Task.Delay(1000);
      } finally {
        pipe?.Dispose();
      }
    }
  }

  // Reads one message, passes it to the handler and writes the answer back when one is expected.
  private async Task Serve(NamedPipeServerStream pipe) {
    using (pipe) {
      try {
        var header = await ReadExactly(pipe, 4);
        var length = header == null ? 0 : BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > MaxMessageBytes) {
          return;
        }
        var body = await ReadExactly(pipe, length);
        if (body == null) {
          return;
        }

        var text = Encoding.UTF8.GetString(body);
        var newline = text.IndexOf('\n');
        if (newline <= 0) {
          return;
        }
        var eventName = text.Substring(0, newline).Trim();
        if (JsonNode.Parse(text.Substring(newline + 1)) is not JsonObject payload) {
          return;
        }

        // The hook never writes again, so a read that completes means it is gone.
        using var gone = new CancellationTokenSource();
        var waitsForAnswer = Array.IndexOf(AnsweredEvents, eventName) >= 0;
        if (waitsForAnswer) {
          _ = Task.Run(async () => {
            try {
              await pipe.ReadExactlyAsync(new byte[1], _cts.Token);
            } catch (Exception) {
            }
            try {
              gone.Cancel();
            } catch (ObjectDisposedException) {
            }
          });
        }

        var answer = await _handler(eventName, payload, gone.Token) ?? "";
        if (Array.IndexOf(AnsweredEvents, eventName) >= 0 && pipe.IsConnected) {
          var bytes = Encoding.UTF8.GetBytes(answer);
          await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), _cts.Token);
          await pipe.WriteAsync(bytes, _cts.Token);
          await pipe.FlushAsync(_cts.Token);
        }
      } catch (Exception ex) when (ex is IOException ||
                                   ex is OperationCanceledException ||
                                   ex is System.Text.Json.JsonException) {
        // The client went away (Claude Code canceled the hook) or sent something unreadable.
      } catch (Exception ex) {
        PluginLog.Warning($"[HookServer] {ex.GetType().Name}: {ex.Message}");
      }
    }
  }

  // Reads exactly count bytes, or returns null when the stream ends first.
  private async Task<byte[]> ReadExactly(Stream stream, int count) {
    var buffer = new byte[count];
    var offset = 0;
    while (offset < count) {
      var n = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), _cts.Token);
      if (n == 0) {
        return null;
      }
      offset += n;
    }
    return buffer;
  }

  // Stops accepting clients. The token source is not disposed: clients still being served observe
  // the cancellation.
  public void Dispose() => _cts.Cancel();
}
