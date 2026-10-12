using System.Runtime.InteropServices;
using static DialedKeyboard.Native.NativeMethods;

namespace DialedKeyboard.Native;

/// <summary>
/// Notices input that didn't come from the on-screen keyboard: mouse clicks outside it
/// (the caret probably moved) and physical key presses. The keyboard then forgets
/// what it thinks is around the caret.
/// </summary>
public sealed class InputWatcher : IDisposable
{
    readonly IntPtr _ownWindow;
    readonly HookProc _mouseProc;   // kept in fields so the GC doesn't collect the callbacks
    readonly HookProc _keyboardProc;
    IntPtr _mouseHook;
    IntPtr _keyboardHook;

    public event Action? ExternalInput;

    public InputWatcher(IntPtr ownWindow)
    {
        _ownWindow = ownWindow;
        _mouseProc = OnMouse;
        _keyboardProc = OnKeyboard;
        IntPtr module = GetModuleHandleW(null);
        _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, _keyboardProc, module, 0);
    }

    IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (GetWindowRect(_ownWindow, out var r) &&
                !(info.pt.X >= r.Left && info.pt.X < r.Right && info.pt.Y >= r.Top && info.pt.Y < r.Bottom))
            {
                ExternalInput?.Invoke();
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    IntPtr OnKeyboard(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLKHF_INJECTED) == 0)
                ExternalInput?.Invoke();
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        _mouseHook = _keyboardHook = IntPtr.Zero;
    }
}
