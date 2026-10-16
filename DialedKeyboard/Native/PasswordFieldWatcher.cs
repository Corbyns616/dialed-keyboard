using System.Runtime.InteropServices;
using static DialedKeyboard.Native.NativeMethods;

namespace DialedKeyboard.Native;

/// <summary>
/// Follows keyboard focus in other apps and reports whether it is in a password box. Apps
/// mark password boxes as "protected" for screen readers (browsers, Win32, WPF and Windows
/// Security prompts all do), which is what this checks.
/// </summary>
public sealed class PasswordFieldWatcher : IDisposable
{
    readonly WinEventProc _proc; // kept in a field so the GC doesn't collect the callback
    IntPtr _hook;
    readonly object _lock = new();
    readonly AutoResetEvent _wake = new(false);
    (IntPtr Hwnd, int IdObject, int IdChild)? _pending;
    volatile bool _disposed;
    bool _last;

    /// <summary>Raised on a background thread when focus moves into or out of a password box.</summary>
    public event Action<bool>? Changed;

    /// <summary>Call on a thread with a message loop: Windows delivers focus events there.</summary>
    public PasswordFieldWatcher()
    {
        _proc = OnFocus;
        _hook = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        // Asking an app about its focused box is a call into that app, which can be slow or hang,
        // so it happens here rather than on the keyboard's thread.
        var worker = new Thread(Work) { IsBackground = true, Name = "PasswordFieldWatcher" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    void OnFocus(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (_lock) _pending = (hwnd, idObject, idChild);
        _wake.Set();
    }

    void Work()
    {
        while (true)
        {
            _wake.WaitOne();
            if (_disposed) return;
            (IntPtr Hwnd, int IdObject, int IdChild)? focus;
            lock (_lock) (focus, _pending) = (_pending, null);
            if (focus is not { } f) continue;
            bool isPassword = IsProtected(f.Hwnd, f.IdObject, f.IdChild);
            if (isPassword == _last || _disposed) continue;
            _last = isPassword;
            Changed?.Invoke(isPassword);
        }
    }

    static bool IsProtected(IntPtr hwnd, int idObject, int idChild)
    {
        Accessibility.IAccessible? acc = null;
        try
        {
            if (AccessibleObjectFromEvent(hwnd, idObject, idChild, out acc, out var child) != 0 || acc == null)
                return false;
            return acc.get_accState(child) is int state && (state & STATE_SYSTEM_PROTECTED) != 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return false; // the box or its app went away
        }
        finally
        {
            if (acc != null) Marshal.ReleaseComObject(acc);
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _disposed = true;
        _wake.Set();
    }
}
