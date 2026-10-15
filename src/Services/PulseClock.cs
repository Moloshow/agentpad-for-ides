using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Loupedeck.ClaudeCodePlugin.Services;

// Drives the slow glow of busy tiles: a value going 0 -> 1 -> 0 every few seconds. It ticks only
// while some session works or waits, a few times a second (the glow is time based, so the rate only
// sets smoothness)
public sealed class PulseClock : IDisposable {
  private static readonly Lazy<PulseClock> _instance = new(() => new PulseClock());
  public static PulseClock Instance => _instance.Value;

  private const int TickMilliseconds = 250;
  private const double PeriodSeconds = 2.4;

  private readonly Stopwatch _clock = Stopwatch.StartNew();
  private readonly Timer _timer;
  private readonly object _gate = new();
  private bool _ticking;

  // Current glow intensity, 0..1
  public double Value { get; private set; }

  // Raised on every tick with the new Value
  public event Action<double> Tick;

  private PulseClock() {
    _timer = new Timer(_ => OnTimer());
    ClaudeSessionService.Instance.OnSessionsChanged += Update;
    Update();
  }

  private void Update() {
    var needed = ClaudeSessionService.Instance.Sessions.Any(s => s.IsWorking || s.NeedsUser);
    lock (_gate) {
      if (needed == _ticking) {
        return;
      }
      _ticking = needed;
      if (needed) {
        _timer.Change(0, TickMilliseconds);
      } else {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        Value = 0;
      }
    }
  }

  private void OnTimer() {
    var phase = _clock.Elapsed.TotalSeconds % PeriodSeconds / PeriodSeconds;
    Value = phase < 0.5 ? phase * 2 : (1 - phase) * 2;
    Tick?.Invoke(Value);
  }

  public void Dispose() => _timer.Dispose();
}
