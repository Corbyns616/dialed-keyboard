namespace DialedKeyboard.Native;

public static class NoActivate
{
    /// <summary>Makes a window always-on-top and unable to take focus, so clicks go "through" to the app being typed into.</summary>
    public static void Apply(IntPtr hwnd)
    {
        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
    }
}
