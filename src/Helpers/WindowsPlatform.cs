using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Loupedeck.ClaudeCodePlugin.Helpers;

// Win32 helpers: finds the editor window a Claude Code session runs in, brings it to the front,
// sends keystrokes to it, and reads SQLite files through the SQLite that ships with Windows.
internal static class WindowsPlatform {
  // ---------------------------------------------------------------- Process tree

  private const uint Th32csSnapProcess = 0x00000002;

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct ProcessEntry32 {
    public uint Size;
    public uint Usage;
    public uint ProcessId;
    public IntPtr DefaultHeapId;
    public uint ModuleId;
    public uint Threads;
    public uint ParentProcessId;
    public int PriClassBase;
    public uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string ExeFile;
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
  private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
  private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

  [DllImport("kernel32.dll")]
  private static extern bool CloseHandle(IntPtr handle);

  // Maps the pid of every running process to its parent pid.
  private static Dictionary<int, int> GetParents() {
    var parents = new Dictionary<int, int>();
    var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
    if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) {
      return parents;
    }
    try {
      var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
      if (Process32FirstW(snapshot, ref entry)) {
        do {
          parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        while (Process32NextW(snapshot, ref entry));
      }
    } finally {
      CloseHandle(snapshot);
    }
    return parents;
  }

  // Whether a process with this pid is running.
  public static bool IsAlive(int pid) {
    try {
      using var p = Process.GetProcessById(pid);
      return !p.HasExited;
    } catch {
      return false;
    }
  }

  // ---------------------------------------------------------------- Windows

  private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

  [DllImport("user32.dll")]
  private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

  [DllImport("user32.dll")]
  private static extern bool IsWindowVisible(IntPtr hWnd);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

  [DllImport("user32.dll")]
  private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

  [DllImport("user32.dll")]
  private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

  [DllImport("user32.dll")]
  private static extern bool SetForegroundWindow(IntPtr hWnd);

  [DllImport("user32.dll")]
  private static extern IntPtr GetForegroundWindow();

  [DllImport("user32.dll")]
  private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

  [DllImport("user32.dll")]
  private static extern bool IsIconic(IntPtr hWnd);

  private const uint GwOwner = 4;
  private const int SwRestore = 9;

  // Visible, titled, unowned top-level windows: (handle, owning pid, title).
  private static List<(IntPtr Handle, int Pid, string Title)> GetTopLevelWindows() {
    var windows = new List<(IntPtr, int, string)>();
    EnumWindows((hWnd, _) => {
      if (!IsWindowVisible(hWnd) || GetWindow(hWnd, GwOwner) != IntPtr.Zero) {
        return true;
      }
      var sb = new StringBuilder(512);
      if (GetWindowTextW(hWnd, sb, sb.Capacity) == 0) {
        return true;
      }
      GetWindowThreadProcessId(hWnd, out var pid);
      windows.Add((hWnd, (int)pid, sb.ToString()));
      return true;
    }, IntPtr.Zero);
    return windows;
  }

  // The editor window hosting a Claude Code process: a window of the nearest ancestor process that
  // owns windows (the editor's main process), preferring one whose title names the project folder.
  public static IntPtr FindEditorWindow(int claudePid, string folderName) {
    var parents = GetParents();
    var windows = GetTopLevelWindows();

    var pid = claudePid;
    for (var depth = 0;
        depth < 8 && parents.TryGetValue(pid, out var parent) && parent > 4;
        depth++) {
      pid = parent;
      var own = windows.Where(w => w.Pid == pid).ToList();
      if (own.Count == 0) {
        continue;
      }
      if (!string.IsNullOrEmpty(folderName)) {
        var match = own.FirstOrDefault(
            w => w.Title.Contains(folderName, StringComparison.OrdinalIgnoreCase));
        if (match.Handle != IntPtr.Zero) {
          return match.Handle;
        }
      }
      return own[0].Handle;
    }
    return IntPtr.Zero;
  }

  public static bool IsForeground(IntPtr hWnd) =>
      hWnd != IntPtr.Zero && GetForegroundWindow() == hWnd;

  // Process names of the VS Code-based editors the Claude Code extension runs in.
  private static readonly string[] EditorProcesses = {
    "Antigravity IDE", "Antigravity", "Code", "Code - Insiders", "Cursor", "Windsurf"
  };

  // The foreground window when it belongs to one of those editors, else IntPtr.Zero.
  public static IntPtr ForegroundEditorWindow() {
    var hWnd = GetForegroundWindow();
    if (hWnd == IntPtr.Zero) {
      return IntPtr.Zero;
    }
    GetWindowThreadProcessId(hWnd, out var pid);
    try {
      using var p = Process.GetProcessById((int)pid);
      return EditorProcesses.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)
          ? hWnd
          : IntPtr.Zero;
    } catch {
      return IntPtr.Zero;
    }
  }

  // Restores the window if minimized and brings it to the foreground.
  public static void Focus(IntPtr hWnd) {
    if (hWnd == IntPtr.Zero) {
      return;
    }
    if (IsIconic(hWnd)) {
      ShowWindow(hWnd, SwRestore);
    }

    // Windows only lets the process that received the last input change the foreground window;
    // a synthetic Alt press lifts that restriction for the Logi Plugin Service.
    SendInputs(KeyInput(VkMenu, false), KeyInput(VkMenu, true));
    SetForegroundWindow(hWnd);
  }

  // ---------------------------------------------------------------- Keystrokes

  public const ushort VkShift = 0x10;
  public const ushort VkControl = 0x11;
  public const ushort VkMenu = 0x12;
  public const ushort VkEscape = 0x1B;
  public const ushort VkF13 = 0x7C;

  private const uint InputKeyboard = 1;
  private const uint KeyEventFKeyUp = 0x0002;

  [StructLayout(LayoutKind.Sequential)]
  private struct KeybdInput {
    public ushort Vk;
    public ushort Scan;
    public uint Flags;
    public uint Time;
    public IntPtr ExtraInfo;
  }

  // INPUT is a union; the mouse variant is the largest member and sets the struct size (64-bit
  // layout).
  [StructLayout(LayoutKind.Explicit, Size = 40)]
  private struct Input {
    [FieldOffset(0)] public uint Type;
    [FieldOffset(8)] public KeybdInput Ki;
  }

  [DllImport("user32.dll", SetLastError = true)]
  private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

  private static Input KeyInput(ushort vk, bool up) =>
      new() {
        Type = InputKeyboard,
        Ki = new KeybdInput { Vk = vk, Flags = up ? KeyEventFKeyUp : 0 }
      };

  private static void SendInputs(params Input[] inputs) =>
      SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

  // Presses the modifiers and the key, then releases them in reverse order.
  public static void SendShortcut(ushort key, params ushort[] modifiers) {
    var inputs = new List<Input>();
    inputs.AddRange(modifiers.Select(m => KeyInput(m, false)));
    inputs.Add(KeyInput(key, false));
    inputs.Add(KeyInput(key, true));
    inputs.AddRange(modifiers.Reverse().Select(m => KeyInput(m, true)));
    SendInputs(inputs.ToArray());
  }

  public const ushort VkReturn = 0x0D;
  private const uint KeyEventFUnicode = 0x0004;

  // Types text into the focused control as Unicode characters, so it works with any keyboard
  // layout and accented characters.
  public static void TypeText(string text) {
    var inputs = new List<Input>();
    foreach (var ch in text) {
      inputs.Add(new Input {
        Type = InputKeyboard,
        Ki = new KeybdInput { Scan = ch, Flags = KeyEventFUnicode }
      });
      inputs.Add(new Input {
        Type = InputKeyboard,
        Ki = new KeybdInput { Scan = ch, Flags = KeyEventFUnicode | KeyEventFKeyUp }
      });
    }
    SendInputs(inputs.ToArray());
  }

  // Focuses the window, gives it a moment to take focus, then sends the shortcut.
  public static void SendShortcutTo(IntPtr hWnd, ushort key, params ushort[] modifiers) {
    if (!IsForeground(hWnd)) {
      Focus(hWnd);
      Thread.Sleep(250);
    }
    SendShortcut(key, modifiers);
  }

  // ---------------------------------------------------------------- SQLite

  // winsqlite3.dll ships with Windows 10 and later, so no SQLite library has to be bundled.
  private const string WinSqlite = "winsqlite3.dll";
  private const int SqliteOk = 0;
  private const int SqliteRow = 100;
  private const int SqliteOpenReadOnly = 0x00000001;

  [DllImport(WinSqlite, CallingConvention = CallingConvention.StdCall)]
  private static extern int sqlite3_open_v2(
      [MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags,
      IntPtr vfs);

  [DllImport(WinSqlite, CallingConvention = CallingConvention.StdCall)]
  private static extern int sqlite3_close(IntPtr db);

  [DllImport(WinSqlite, CallingConvention = CallingConvention.StdCall)]
  private static extern int sqlite3_prepare_v2(
      IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int nByte, out IntPtr stmt,
      IntPtr tail);

  [DllImport(WinSqlite, CallingConvention = CallingConvention.StdCall)]
  private static extern int sqlite3_step(IntPtr stmt);

  [DllImport(WinSqlite, CallingConvention = CallingConvention.StdCall)]
  private static extern int sqlite3_column_count(IntPtr stmt);

  [DllImport(WinSqlite, CallingConvention = CallingConvention.StdCall)]
  private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

  [DllImport(WinSqlite, CallingConvention = CallingConvention.StdCall)]
  private static extern int sqlite3_finalize(IntPtr stmt);

  // Runs a query on a database opened read-only and returns its rows, every column as text.
  public static List<string[]> QuerySqlite(string path, string sql) {
    var rows = new List<string[]>();
    if (sqlite3_open_v2(path, out var db, SqliteOpenReadOnly, IntPtr.Zero) != SqliteOk) {
      sqlite3_close(db);
      return rows;
    }

    try {
      if (sqlite3_prepare_v2(db, sql, -1, out var stmt, IntPtr.Zero) != SqliteOk) {
        return rows;
      }

      try {
        var columns = sqlite3_column_count(stmt);
        while (sqlite3_step(stmt) == SqliteRow) {
          var row = new string[columns];
          for (var i = 0; i < columns; i++) {
            row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, i)) ?? "";
          }
          rows.Add(row);
        }
      } finally {
        sqlite3_finalize(stmt);
      }
    } finally {
      sqlite3_close(db);
    }

    return rows;
  }
}
