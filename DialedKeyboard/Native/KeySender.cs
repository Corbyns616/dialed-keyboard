using System.Runtime.InteropServices;
using DialedKeyboard.Engine;
using static DialedKeyboard.Native.NativeMethods;

namespace DialedKeyboard.Native;

/// <summary>Types into whichever app has focus, using SendInput.</summary>
public sealed class KeySender : ITextOutput
{
    public const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10,
        VK_CONTROL = 0x11, VK_MENU = 0x12, VK_ESCAPE = 0x1B, VK_SPACE = 0x20,
        VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_END = 0x23, VK_HOME = 0x24, VK_DELETE = 0x2E, VK_LWIN = 0x5B, VK_RCONTROL = 0xA3;

    static readonly int InputSize = Marshal.SizeOf<INPUT>();

    public void Text(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            if (c == '\n')
            {
                AddVk(inputs, VK_RETURN, up: false);
                AddVk(inputs, VK_RETURN, up: true);
            }
            else
            {
                inputs.Add(Unicode(c, up: false));
                inputs.Add(Unicode(c, up: true));
            }
        }
        Send(inputs);
    }

    public void Backspace(int count)
    {
        var inputs = new List<INPUT>(count * 2);
        for (int i = 0; i < count; i++)
        {
            AddVk(inputs, VK_BACK, up: false);
            AddVk(inputs, VK_BACK, up: true);
        }
        Send(inputs);
    }

    public void Enter() => Press(VK_RETURN, Array.Empty<ushort>());

    /// <summary>Sends just the key-down or key-up half of a key, for keys held like a real one.</summary>
    public void Hold(ushort vk, bool down)
    {
        var inputs = new List<INPUT>(1);
        AddVk(inputs, vk, up: !down);
        Send(inputs);
    }

    /// <summary>Presses <paramref name="vk"/> while holding <paramref name="modifiers"/> (e.g. Ctrl+C).</summary>
    public void Press(ushort vk, IReadOnlyList<ushort> modifiers)
    {
        var inputs = new List<INPUT>();
        foreach (var m in modifiers) AddVk(inputs, m, up: false);
        AddVk(inputs, vk, up: false);
        AddVk(inputs, vk, up: true);
        for (int i = modifiers.Count - 1; i >= 0; i--) AddVk(inputs, modifiers[i], up: true);
        Send(inputs);
    }

    /// <summary>The virtual-key code for a character on the current keyboard layout, or 0.</summary>
    public static ushort VkForChar(char c)
    {
        short r = VkKeyScanW(c);
        return r == -1 ? (ushort)0 : (ushort)(r & 0xFF);
    }

    static bool IsExtended(ushort vk) =>
        vk is >= 0x21 and <= 0x28   // PgUp, PgDn, End, Home, arrows
            or 0x2D or 0x2E          // Insert, Delete
            or VK_LWIN or 0x5C
            or VK_RCONTROL or 0xA5;  // right Ctrl, right Alt

    static void AddVk(List<INPUT> list, ushort vk, bool up)
    {
        uint flags = (up ? KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        // Include the scan code a physical key would send, so apps that read it (or tell left from right Ctrl) see the right key.
        var scan = (ushort)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
        list.Add(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } });
    }

    static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } },
    };

    static void Send(List<INPUT> inputs)
    {
        if (inputs.Count > 0)
            SendInput((uint)inputs.Count, inputs.ToArray(), InputSize);
    }
}
