using System;
using System.Collections.Generic;
using System.Linq;
using Loupedeck.ClaudeCodePlugin.Models;

namespace Loupedeck.ClaudeCodePlugin.Services;

// Which session sits on which tile. Sessions keep the order they appeared in and always fill the
// first tiles: when one ends, the next ones move up. While a session folder is open nothing moves,
// so the keys under the finger stay the same; the gaps close once it is closed.
public sealed class SlotBoard {
  public const int SlotCount = 16;

  // Session keys in order of appearance
  private readonly List<string> _order = new();

  // Slot number (1-based) to session key
  private readonly Dictionary<int, string> _seats = new();

  // Session key on the given slot, or null if the slot is empty
  public string KeyAt(int slot) => _seats.TryGetValue(slot, out var key) ? key : null;

  // Slot the session key sits on, or null if it has none
  public int? SlotOf(string key) {
    if (string.IsNullOrEmpty(key)) {
      return null;
    }

    foreach (var (slot, seated) in _seats) {
      if (string.Equals(seated, key, StringComparison.OrdinalIgnoreCase)) {
        return slot;
      }
    }
    return null;
  }

  // Seats the current sessions; while heldKey is seated and present, seated sessions stay put
  public void Update(IReadOnlyList<ClaudeSession> sessions, string heldKey) {
    var present = new HashSet<string>(
        sessions.Select(s => s.SlotKey), StringComparer.OrdinalIgnoreCase);
    _order.RemoveAll(key => !present.Contains(key));
    foreach (var s in sessions.Where(
        s => !_order.Contains(s.SlotKey, StringComparer.OrdinalIgnoreCase))) {
      _order.Add(s.SlotKey);
    }

    var heldSlot = SlotOf(heldKey);
    if (heldSlot.HasValue && present.Contains(heldKey)) {
      // Keep every seated session where it is; newcomers take free tiles
      var goneSlots = _seats.Where(kv => !present.Contains(kv.Value)).Select(kv => kv.Key).ToList();
      foreach (var gone in goneSlots) {
        _seats.Remove(gone);
      }
      foreach (var key in _order.Where(k => SlotOf(k) == null)) {
        var free = Enumerable.Range(1, SlotCount).FirstOrDefault(n => !_seats.ContainsKey(n));
        if (free == 0) {
          break;
        }

        _seats[free] = key;
      }
      return;
    }

    _seats.Clear();
    for (var i = 0; i < _order.Count && i < SlotCount; i++) {
      _seats[i + 1] = _order[i];
    }
  }
}
