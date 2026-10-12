using DialedKeyboard.Native;

namespace DialedKeyboard;

public enum KeyKind
{
    Char, Space, Backspace, DeleteWord, Enter,
    /// <summary>Presses <see cref="KeyDef.Vk"/> (Esc, Tab, arrows…) with any held modifiers.</summary>
    Nav,
    Shift, Ctrl, Alt, Win, Page, Spacer,
    /// <summary>Opens the clipboard panel: Copy/Cut/Paste/Select all and recent copies.</summary>
    Clipboard,
}

/// <param name="Hint">The small symbol in the corner, typed by a long press or right-click.</param>
/// <param name="Width">Relative width; each row adds up to 10.</param>
public sealed record KeyDef(KeyKind Kind, string Label = "", char Base = '\0', char? Hint = null, double Width = 1, ushort Vk = 0);

/// <summary>A Samsung-style phone layout: an ABC page and a !#1 symbols page.</summary>
public static class KeyLayout
{
    /// <summary>Relative row heights; the number row is shorter, as on a phone.</summary>
    public static readonly double[] RowHeights = { 0.86, 1, 1, 1, 1 };

    static KeyDef C(char b, char? hint = null) => new(KeyKind.Char, "", b, hint);
    static KeyDef Gap(double width) => new(KeyKind.Spacer, Width: width);
    static KeyDef Special(KeyKind kind, string label, double width = 1, ushort vk = 0) => new(kind, label, Width: width, Vk: vk);

    static KeyDef[] Digits() => "1234567890".Select(c => C(c)).ToArray();

    public static readonly KeyDef[][] Letters =
    {
        Digits(),
        new[]
        {
            C('q', '+'), C('w', '×'), C('e', '÷'), C('r', '='), C('t', '/'),
            C('y', '_'), C('u', '<'), C('i', '>'), C('o', '['), C('p', ']'),
        },
        new[]
        {
            Gap(0.5),
            C('a', '!'), C('s', '@'), C('d', '#'), C('f', '$'), C('g', '%'),
            C('h', '&'), C('j', '*'), C('k', '('), C('l', ')'),
            Gap(0.5),
        },
        new[]
        {
            Special(KeyKind.Shift, "⇧", 1.5),
            C('z', '-'), C('x', '\''), C('c', '"'), C('v', ':'), C('b', ';'), C('n', ','), C('m', '?'),
            Special(KeyKind.Backspace, "⌫", 1.5),
        },
        new[]
        {
            Special(KeyKind.Page, "!#1", 1.5),
            C('?', '-'),
            Special(KeyKind.Space, "English (US)", 5),
            C('.'),
            Special(KeyKind.Enter, "↵", 1.5),
        },
    };

    /// <summary>Keys at the left of the suggestion strip, on both pages so shortcuts like Ctrl+C stay one tap away.</summary>
    public static readonly KeyDef[] StripKeys =
    {
        Special(KeyKind.Nav, "Esc", vk: KeySender.VK_ESCAPE),
        Special(KeyKind.Ctrl, "Ctrl"),
        Special(KeyKind.Alt, "Alt"),
        Special(KeyKind.Clipboard, "Clipboard"),
    };

    /// <summary>Symbols missing from the ABC page, plus the desktop keys a phone doesn't have.</summary>
    public static readonly KeyDef[][] Symbols =
    {
        new[]
        {
            Special(KeyKind.Nav, "Tab", 1.5, KeySender.VK_TAB),
            Special(KeyKind.Win, "⊞"),
            Special(KeyKind.Nav, "Home", vk: KeySender.VK_HOME),
            Special(KeyKind.Nav, "End", vk: KeySender.VK_END),
            Special(KeyKind.Nav, "Del", 1.5, KeySender.VK_DELETE),
            Special(KeyKind.Nav, "←", vk: KeySender.VK_LEFT),
            Special(KeyKind.Nav, "↑", vk: KeySender.VK_UP),
            Special(KeyKind.Nav, "↓", vk: KeySender.VK_DOWN),
            Special(KeyKind.Nav, "→", vk: KeySender.VK_RIGHT),
        },
        "+×÷=/_<>[]".Select(c => C(c)).ToArray(),
        "!@#$%^&*()".Select(c => C(c)).ToArray(),
        new[]
        {
            Special(KeyKind.DeleteWord, "⌫ Word", 1.5),
            C('`'), C('~'), C('\\'), C('|'), C('{'), C('}'), C('?', ';'),
            Special(KeyKind.Backspace, "⌫", 1.5),
        },
        new[]
        {
            Special(KeyKind.Page, "ABC", 1.5),
            C(','),
            Special(KeyKind.Space, "English (US)", 5),
            C('.'),
            Special(KeyKind.Enter, "↵", 1.5),
        },
    };
}
