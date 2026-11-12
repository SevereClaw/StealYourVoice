using System;
using System.Runtime.InteropServices;

namespace StealYourVoice;

/// <summary>
/// Global low-level mouse hook. Used only so the click-through talkers list can be
/// grabbed with Shift held; everything else passes through untouched.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    public const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_MOUSEWHEEL = 0x20A;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public IntPtr Extra;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    public static bool ShiftDown => (GetAsyncKeyState(0x10) & 0x8000) != 0;
    public static bool CtrlDown => (GetAsyncKeyState(0x11) & 0x8000) != 0;

    private readonly HookProc _proc; // keep the delegate alive
    private IntPtr _hook;

    /// <summary>(message, x, y, wheel delta) → true to swallow the event.</summary>
    public Func<int, int, int, int, bool>? Handler;

    /// <summary>True while something is being dragged — then every movement matters; otherwise ~30 per second is plenty.</summary>
    public Func<bool>? EveryMove;

    private long _lastMove;

    public MouseHook()
    {
        _proc = Callback;
        _hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, GetModuleHandle(null), 0);
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Handler != null)
        {
            try
            {
                int msg = wParam.ToInt32();
                bool wanted = msg is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_MOUSEWHEEL;
                if (msg == WM_MOUSEMOVE)
                {
                    // gaming mice report up to 1000 moves a second — look at ~30 of them unless dragging
                    long now = Environment.TickCount64;
                    wanted = now - _lastMove >= 30 || (EveryMove?.Invoke() ?? false);
                    if (wanted) _lastMove = now;
                }
                if (wanted)
                {
                    // read the three fields straight from memory instead of copying the whole struct
                    int x = Marshal.ReadInt32(lParam, 0), y = Marshal.ReadInt32(lParam, 4);
                    int delta = msg == WM_MOUSEWHEEL ? (short)((uint)Marshal.ReadInt32(lParam, 8) >> 16) : 0;
                    if (Handler(msg, x, y, delta)) return (IntPtr)1;
                }
            }
            catch { /* never break the user's mouse */ }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
