# Dialed Keyboard

A free, phone-style on-screen keyboard for Windows 10 and 11. It is for anyone who finds a
physical keyboard hard to use and wants to type with a mouse, trackball, touchscreen, head
pointer or other pointing device.

The keyboard never takes focus away from the app you are typing into, so you can click a text
box anywhere and type.

## Features

- **Phone-style layout** with an ABC page and a !#1 page for numbers and symbols. Long-press or
  right-click a key for the symbol in its corner.
- **Swipe typing:** press a letter and slide across the others to type a whole word.
- **Word suggestions** that learn the words you use, including names and spellings that aren't
  in the dictionary.
- **Text shortcuts:** type a short abbreviation and it expands into a longer phrase.
- **Auto-capitalisation**, smart punctuation and double-space for a full stop. You can turn each
  one off.
- **Clipboard panel** with big Copy, Cut, Paste and Select all buttons, plus your recent copies.
- **Sticky Ctrl** and hold-to-repeat Backspace, so you don't need to hold keys down.
- A **floating button** that shrinks the keyboard out of the way, and **Ctrl+Alt+K** to show or
  hide it. You can move and resize the keyboard.
- **Light and dark themes**, adjustable transparency and optional mechanical key sounds.

## Install

You need Windows 10 or 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

### Standard install (recommended)

Download or clone this repository, open PowerShell in the folder and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Windows shows one administrator (UAC) prompt. The script:

1. builds the keyboard;
2. creates a code-signing certificate that exists only on your PC and tells Windows to trust it;
3. installs the keyboard to `C:\Program Files\DialedKeyboard`, signs it, adds a Start menu
   shortcut and starts it.

The keyboard needs the certificate and the Program Files location so it can type into Windows
password prompts and administrator windows, the way the built-in Windows On-Screen Keyboard does.
Windows blocks typing into those windows from ordinary apps.

Run the same command again to update after downloading a newer version.

### Try it without installing

```powershell
dotnet run --project DialedKeyboard
```

This version works in most apps but cannot type into Windows password prompts or administrator
windows.

## Privacy

Everything stays on your computer. Dialed Keyboard does not connect to the internet and does not
collect or send anything.

- The words it learns, your text shortcuts and your settings are saved in
  `%AppData%\DialedKeyboard`. Delete that folder to clear them.
- Clipboard history is kept in memory only and is forgotten when the keyboard closes. It skips
  anything that apps mark as private, such as copies from password managers.
- You can pause learning in Settings.

## Uninstall

1. Close the keyboard from its tray icon.
2. Delete `C:\Program Files\DialedKeyboard` and the "DialedKeyboard" Start menu shortcut.
3. Optionally delete `%AppData%\DialedKeyboard` to remove your learned words and settings.
4. Optionally remove the "DialedKeyboard Local Code Signing" certificate: open
   `certlm.msc` and look under Personal, Trusted Root Certification Authorities and Trusted
   Publishers.

## License

Dialed Keyboard is free and open source under the [MIT License](LICENSE). The key sounds and
word list come from other projects. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
