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

You need Windows 10 or 11 (64-bit). Nothing else needs to be installed first.

1. **[Download DialedKeyboard-Setup.exe](https://github.com/Corbyns616/dialed-keyboard/releases/latest/download/DialedKeyboard-Setup.exe)**
   (about 50 MB). Older versions are on the [Releases page](https://github.com/Corbyns616/dialed-keyboard/releases).
2. Open the downloaded file. Windows may show a blue box saying **"Windows protected your PC"**.
   This happens because Dialed Keyboard is a free project and isn't signed by a paid publisher.
   Click **More info**, then **Run anyway**. If your browser warns about the download, choose
   **Keep**.
3. Click **Yes** when Windows asks if the app can make changes.
4. Choose whether it should start when you sign in to Windows, then click **Install**. When setup
   finishes, click **Finish** and the keyboard opens.

Dialed Keyboard is now in your Start menu. To update it, download the newest setup and run it
again. Your learned words and settings are kept.

### Why setup asks for administrator access

Windows blocks ordinary apps from typing into password prompts and administrator windows. Like the
built-in Windows On-Screen Keyboard, Dialed Keyboard needs special permission (called uiAccess) to
type there. Windows only gives that permission to an app that is installed in Program Files and
digitally signed. So setup:

1. installs the keyboard to `C:\Program Files\DialedKeyboard`;
2. creates a signing certificate on your PC, signs the keyboard with it and tells Windows to trust
   it;
3. deletes the certificate's private key straight away, so nothing else can ever be signed with it.

Uninstalling removes the certificate again.

## Uninstall

1. Open **Settings > Apps > Installed apps**, find **Dialed Keyboard** and click **Uninstall**.
2. Optionally delete `%AppData%\DialedKeyboard` to remove your learned words and settings.

## Build from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

- `dotnet run --project DialedKeyboard` runs it without installing. This copy can't type into
  password prompts or administrator windows.
- `powershell -ExecutionPolicy Bypass -File .\install.ps1` builds and installs it with uiAccess.
  Run it again after changing the code.
- `powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1` builds
  `installer\Output\DialedKeyboard-Setup.exe`. It also needs Inno Setup 6
  (`winget install JRSoftware.InnoSetup`).

## Privacy

Everything stays on your computer. Dialed Keyboard does not connect to the internet and does not
collect or send anything.

- The words it learns, your text shortcuts and your settings are saved in
  `%AppData%\DialedKeyboard`. Delete that folder to clear them.
- Clipboard history is kept in memory only and is forgotten when the keyboard closes. It skips
  anything that apps mark as private, such as copies from password managers.
- You can pause learning in Settings.

## License
