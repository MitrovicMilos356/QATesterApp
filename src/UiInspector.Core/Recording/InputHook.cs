using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace UiInspector.Core.Recording;

public enum MouseButton
{
    Left,
    Right,
}

/// <summary>A raw input event from the hook. <see cref="Time"/> is the system tick count in ms.</summary>
public abstract record RawInput(long Time);

public sealed record RawMouse(MouseButton Button, bool Down, Point Position, long Time) : RawInput(Time);

public sealed record RawKey(KeyChord Chord, KeyKind Kind, long Time) : RawInput(Time);

/// <summary>A printable key was pressed. Deliberately carries no information about which one.</summary>
public sealed record RawTyping(long Time) : RawInput(Time);

/// <summary>Where the recorder gets clicks and key presses from (the real hook, or a test).</summary>
public interface IInputSource : IDisposable
{
    /// <summary>Starts delivering events to <paramref name="sink"/>, which must return quickly.</summary>
    void Start(Action<RawInput> sink);
}

/// <summary>
/// Low-level mouse and keyboard hooks (WH_MOUSE_LL, WH_KEYBOARD_LL), installed only while recording.
/// The callbacks run on a dedicated message-loop thread and only hand events on, so they never
/// delay input. Keys are classified inside the hook; for printable keys only "something was
/// typed" leaves it. Synthetic input (from automation scripts, including the ones this tool
/// generates) is ignored.
/// </summary>
public sealed class InputHook : IInputSource
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const uint LLMHF_INJECTED = 0x01, LLKHF_INJECTED = 0x10;
    private const uint WM_QUIT = 0x0012, PM_NOREMOVE = 0x0000;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    // Kept in fields so the garbage collector doesn't free the delegates while Windows holds them.
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private Action<RawInput>? _sink;
    private Thread? _thread;
    private uint _threadId;

    public InputHook()
    {
        _mouseProc = OnMouse;
        _keyboardProc = OnKeyboard;
    }

    public void Start(Action<RawInput> sink)
    {
        if (_thread is not null) throw new InvalidOperationException("The hook is already running.");
        _sink = sink;
        var started = new TaskCompletionSource();
        _thread = new Thread(() => Run(started)) { IsBackground = true, Name = "Recorder input hook" };
        _thread.Start();
        started.Task.GetAwaiter().GetResult(); // rethrows if a hook couldn't be installed
    }

    private void Run(TaskCompletionSource started)
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE); // make sure the thread has a message queue

        var module = GetModuleHandle(null);
        var mouse = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        var mouseError = Marshal.GetLastWin32Error();
        var keyboard = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        var keyboardError = Marshal.GetLastWin32Error();
        if (mouse == IntPtr.Zero || keyboard == IntPtr.Zero)
        {
            if (mouse != IntPtr.Zero) UnhookWindowsHookEx(mouse);
            if (keyboard != IntPtr.Zero) UnhookWindowsHookEx(keyboard);
            started.SetException(new Win32Exception(mouse == IntPtr.Zero ? mouseError : keyboardError, "Couldn't install the input hook for recording."));
            return;
        }
        started.SetResult();

        // Low-level hook callbacks are delivered through this thread's message loop.
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
        {
        }

        UnhookWindowsHookEx(mouse);
        UnhookWindowsHookEx(keyboard);
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((data.flags & LLMHF_INJECTED) == 0)
            {
                var point = new Point(data.ptX, data.ptY);
                RawMouse? e = (int)wParam switch
                {
                    WM_LBUTTONDOWN => new(MouseButton.Left, true, point, data.time),
                    WM_LBUTTONUP => new(MouseButton.Left, false, point, data.time),
                    WM_RBUTTONDOWN => new(MouseButton.Right, true, point, data.time),
                    WM_RBUTTONUP => new(MouseButton.Right, false, point, data.time),
                    _ => null,
                };
                if (e is not null) _sink?.Invoke(e);
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr OnKeyboard(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((data.flags & LLKHF_INJECTED) == 0)
            {
                var chord = new KeyChord((int)data.vkCode,
                    Ctrl: IsDown(VK_CONTROL), Alt: IsDown(VK_MENU), Shift: IsDown(VK_SHIFT), Win: IsDown(VK_LWIN) || IsDown(VK_RWIN));
                var kind = KeyClassifier.Classify(chord);
                RawInput? e = kind switch
                {
                    KeyKind.Typing => new RawTyping(data.time),
                    KeyKind.Step or KeyKind.StepUnlessTextField => new RawKey(chord, kind, data.time),
                    _ => null,
                };
                if (e is not null) _sink?.Invoke(e);
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_thread is null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(1));
        _thread = null;
        _sink = null;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int ptX;
        public int ptY;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
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
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint removeMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
