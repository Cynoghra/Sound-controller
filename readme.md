# SoundController

A Windows tray application that keeps your chosen audio devices locked. It
restores SteelSeries Sonar (Classic mode) redirections and Windows default
devices when something disturbs them - for example a PS5 controller that
claims the default playback slot every time you plug it in.

## What it does

- Watches SteelSeries Sonar (via its local API) for redirection changes on the
  Game, Chat, Media, Aux, and Mic channels, and restores your saved devices
  when they drift.
- Watches Windows default devices for all roles (Console, Multimedia,
  Communications, playback and recording) and restores drift the same way.
- Runs in the system tray, shows a colored status dot, and offers manual
  **Capture current** / **Apply saved** actions.
- Stores settings by device endpoint ID (not by name, which changes with
  drivers and locales) in `%LOCALAPPDATA%\SoundController\settings.json`.

## Requirements

- Windows 10 1803+ or Windows 11
- .NET SDK 8.0 to build (not needed to run the published exe)
- SteelSeries GG with Sonar enabled, in **Classic mode** (Streamer mode is
  detected and reported as unsupported)

## Build and run

```powershell
dotnet build SoundController.sln
dotnet test SoundController.sln
dotnet run --project src/SoundController/SoundController.csproj
```

## Publish a single-file exe

```powershell
dotnet publish src/SoundController/SoundController.csproj -c Release -p:PublishProfile=FolderProfile
```

This writes a self-contained `publish/SoundController.exe` (no .NET runtime
required on the target machine).

## First run

The app asks whether to capture the current Sonar redirections and Windows
defaults as the locked state. You can re-capture at any time from the tray
menu or the settings window. Everything is editable per channel/role in
Settings; a device set to "(not locked)" is left alone by restore.

## Changing your locked devices

Use the settings window (left-click the tray icon, or right-click it and
choose **Settings...**): pick devices per slot, then **Save** and
**Apply now**. Combos you did not touch keep their previously locked device
when you save - only the slots you actually change take the new value, and
"(not locked)" only clears a slot you deliberately picked. If a saved device
is not in the current device list (for example it is unplugged), the status
line says so and its lock is kept until the device returns.

Changes made directly in the SteelSeries Sonar UI are reverted within about
a second while auto-restore is on - that is the lock working as designed.
This includes channels Sonar itself clears: a locked channel found without
a device ("red, no device" state) is healed automatically. If you prefer
editing inside Sonar's UI, temporarily untick **Auto-restore enabled** in
the tray menu first.

## Status colors (tray dot)

| Color | Meaning |
|---|---|
| Green | Devices match locked state |
| Amber | Restoring, or a saved device is unavailable (waiting for it to return) |
| Red | Sonar disconnected (Windows restore stays active) |
| Gray | Auto-restore disabled |

## Finding the tray icon

Windows 11 hides newly installed tray icons inside the overflow chevron
("**^**" left of the clock) until you pin them. If you do not see
SoundController there:

1. Right-click the taskbar and open **Taskbar settings**.
2. Expand **Other system tray icons**.
3. Switch **SoundController** to **On** - it will then show permanently.

On first run the app also shows a balloon pointing this out. If the icon
still never appears, check
`%LOCALAPPDATA%\SoundController\logs\soundcontroller-YYYYMMDD.log`; a failed
tray start now exits the process cleanly instead of leaving it running
headless.

## Removal

Right-click the tray icon and choose **Remove app data & exit**. After
confirmation it removes the
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` `SoundController` value,
deletes `%LOCALAPPDATA%\SoundController` (settings + logs), and exits. If a
file was still in use, a message names the folder to delete manually after
the app has exited.

The application creates no other registry entries and registers no COM
components, so these two steps are the complete removal story.

## Logs

`%LOCALAPPDATA%\SoundController\logs\soundcontroller-YYYYMMDD.log` (newest 5
kept). If a GG update changes the unofficial Sonar API, this is where the
failure shows up first.

## Project layout

```
src/SoundController/
  Config/          AppSettings model, versioned atomic JSON persistence,
                   LockedSlotMerge (save semantics), AppCleanupService
  Core/            Shared snapshots and restore-plan records
  Sonar/           RestoreEngine (pure diff/plan logic), SonarService
  WindowsAudio/    NAudio endpoint service + isolated PolicyConfig COM interop
  Orchestration/   RestoreCoordinator (debounce, suppression, serialization)
  Autostart/       HKCU Run-key autostart control
  Logging/         Minimal rolling file logger
  Themes/          DarkGreen.xaml (deep forest green theme, keyed menu styles)
  Tray/            TrayController (icon, dark context menu, status dot)
  UI/              SettingsWindow (device pickers, merge-on-save)
tests/             Unit tests for engine decisions, merge semantics, settings
publish/           Single-file exe output (gitignored, produced by dotnet publish)
```

See `AGENTS.md` for project conventions and `TODO.md` for the current task
list and what has not been hardware-verified yet.

## Acknowledgments

This project builds directly on the work of these open-source projects:

- [Steelseries-NET-API](https://github.com/DataNext27/SteelSeries-NET-API) —
  Sonar control, device discovery, and change events (MIT).
- [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) — system tray icon
  for WPF (MIT).
- [NAudio](https://github.com/naudio/NAudio) — Windows audio endpoint
  enumeration and notifications (MIT).
- [AudioDeviceCmdlets](https://github.com/frgnca/AudioDeviceCmdlets) —
  reference for Windows default-device switching (MIT).

The four libraries above are MIT-licensed and used as dependencies.
[SoundSwitch](https://github.com/Belphemur/SoundSwitch) and
[AudioEndPointLibrary](https://github.com/Belphemur/AudioEndPointLibrary)
(GPL-3.0) are credited as reference only - no code from them is included;
they documented the undocumented `IPolicyConfig` COM interface this app's
default-endpoint interop relies on.
