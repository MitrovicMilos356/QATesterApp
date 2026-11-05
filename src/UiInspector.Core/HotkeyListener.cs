using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace UiInspector.Core;

public enum HotkeyCommand
{
    InspectAtCursor,
    InspectFocused,
    InspectDelayed,
    ToggleHover,
}

public sealed record Hotkey(HotkeyCommand Command, string Display, uint Modifiers, uint VirtualKey);

/// <summary>
/// Registers global hotkeys with RegisterHotKey on a dedicated message-loop thread. This needs
/// no low-level keyboard or mouse hook, so it adds no input lag and doesn't trip antivirus heuristics.
/// </summary>
public sealed class HotkeyListener : IDisposable
{
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_F9 = 0x78, VK_F10 = 0x79, VK_F11 = 0x7A, VK_F12 = 0x7B;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_QUIT = 0x0012;
    private const uint PM_NOREMOVE = 0x0000;

    public static readonly IReadOnlyList<Hotkey> Defaults =
    [
        new(HotkeyCommand.InspectAtCursor, "Ctrl+Shift+F9", MOD_CONTROL | MOD_SHIFT, VK_F9),
        new(HotkeyCommand.InspectFocused, "Ctrl+Shift+F10", MOD_CONTROL | MOD_SHIFT, VK_F10),
        new(HotkeyCommand.InspectDelayed, "Ctrl+Shift+F11", MOD_CONTROL | MOD_SHIFT, VK_F11),
        new(HotkeyCommand.ToggleHover, "Ctrl+Shift+F12", MOD_CONTROL | MOD_SHIFT, VK_F12),
    ];

    private readonly Channel<HotkeyCommand> _commands = Channel.CreateUnbounded<HotkeyCommand>();
    private readonly IReadOnlyList<Hotkey> _hotkeys;
    private Thread? _thread;
    private uint _threadId;

    public HotkeyListener(IReadOnlyList<Hotkey>? hotkeys = null) => _hotkeys = hotkeys ?? Defaults;

    public ChannelReader<HotkeyCommand> Commands => _commands.Reader;

    /// <summary>Starts the listener and returns, per hotkey, null on success or the registration error.</summary>
    public IReadOnlyList<(Hotkey Hotkey, string? Error)> Start()
    {
        var started = new TaskCompletionSource<IReadOnlyList<(Hotkey, string?)>>();
        _thread = new Thread(() => Run(started)) { IsBackground = true, Name = "Hotkey listener" };
        _thread.Start();
        return started.Task.GetAwaiter().GetResult();
    }

    private void Run(TaskCompletionSource<IReadOnlyList<(Hotkey, string?)>> started)
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE); // make sure the thread has a message queue

        var results = new List<(Hotkey, string?)>();
        for (var i = 0; i < _hotkeys.Count; i++)
        {
            var hotkey = _hotkeys[i];
            var ok = RegisterHotKey(IntPtr.Zero, i + 1, hotkey.Modifiers | MOD_NOREPEAT, hotkey.VirtualKey);
            results.Add((hotkey, ok ? null : new Win32Exception(Marshal.GetLastWin32Error()).Message));
        }
        started.SetResult(results);

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY)
            {
                var index = (int)msg.wParam - 1;
                if (index >= 0 && index < _hotkeys.Count) _commands.Writer.TryWrite(_hotkeys[index].Command);
            }
        }

        for (var i = 0; i < _hotkeys.Count; i++) UnregisterHotKey(IntPtr.Zero, i + 1);
        _commands.Writer.TryComplete();
    }

    public void Dispose()
    {
        if (_thread is null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(1));
        _thread = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint removeMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
