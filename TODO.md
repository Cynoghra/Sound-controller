# SoundController - Sonar Device Locker

## Goal

Build an always-on Windows tray application that locks both SteelSeries Sonar
Classic-mode redirections and Windows default audio devices. Plugging in a PS5
controller or another audio endpoint must not permanently disturb the saved
setup.

## Decisions

- .NET 8 WPF application targeting Windows x64.
- Self-contained, single-file publish so the finished executable has no runtime
  prerequisite.
- SteelSeries-NET-API for Sonar discovery, redirections, and change events.
- NAudio for Windows audio endpoint discovery and notifications.
- H.NotifyIcon.Wpf for the system tray interface.
- Sonar Classic mode is the initial supported mode.
- Preferences are stored locally as JSON and identify devices by endpoint ID,
  not by display name.

## Proposed Structure

```text
SoundController/
|-- SoundController.sln
|-- src/SoundController/
|   |-- SoundController.csproj
|   |-- App.xaml
|   |-- App.xaml.cs
|   |-- Config/
|   |   |-- AppSettings.cs
|   |   `-- SettingsService.cs
|   |-- Sonar/
|   |   |-- SonarService.cs
|   |   `-- RestoreEngine.cs
|   |-- WindowsAudio/
|   |   `-- WindowsDefaultService.cs
|   |-- Tray/
|   |   |-- TrayIcon.cs
|   |   `-- TrayMenu.xaml
|   `-- UI/
|       |-- SettingsWindow.xaml
|       `-- SettingsWindow.xaml.cs
|-- tests/SoundController.Tests/
`-- README.md
```

## Runtime Behavior

1. On startup, create a single-instance mutex and initialize logging, settings,
   Sonar, Windows audio monitoring, and the tray icon.
2. Let `SonarClient` discover and reconnect to the local SteelSeries GG Sonar
   service. Do not cache the service port.
3. On first run, offer to capture the current Sonar redirections and Windows
   default playback, communication, recording, and recording-communication
   devices as the locked setup.
4. Subscribe to Sonar device and redirection changes. Enable the API's polling
   interval where required for complete event detection.
5. Subscribe to Windows default-device and endpoint changes through NAudio.
6. Debounce related notifications, compare actual state with saved state, and
   restore only values that differ. This avoids feedback loops and needless
   writes.
7. If a saved physical device is disconnected, report it as unavailable and
   wait for it to return rather than choosing an arbitrary fallback.
8. Keep background failures out of the UI thread, log useful diagnostics, and
   expose a clear connected, degraded, or disconnected tray status.

## Tray Interface

- Auto-restore enabled toggle.
- Sonar connection and protection status.
- Capture current setup as locked.
- Apply saved setup now.
- Open settings.
- Start with Windows toggle.
- Open logs.
- Exit.

The settings window will show the saved Windows defaults and each applicable
Sonar Classic channel with a dropdown of available physical endpoints. Device
display names are shown for readability, while endpoint IDs are persisted.

## Implementation Tasks

- [x] 1. Create the solution, WPF project, test project, and NuGet references.
- [x] 2. Add structured logging and local application-data paths.
- [x] 3. Implement versioned JSON settings with atomic writes and validation.
- [x] 4. Implement `SonarService` for connection state, device discovery, and
      Classic-mode redirection reads and writes.
- [x] 5. Implement the testable `RestoreEngine` that calculates and applies the
      smallest required state correction.
- [x] 6. Implement `WindowsDefaultService` for endpoint enumeration,
      notifications, and restoration of all Windows default roles.
- [x] 7. Add debounced event handling and protection against restore feedback
      loops.
- [x] 8. Build the tray icon, context menu, and connection-state indicators.
- [x] 9. Build the settings window and capture/apply workflows.
- [x] 10. Add single-instance behavior and optional current-user autostart.
- [x] 11. Add unit tests for state comparison, unavailable devices, settings
      validation, and restore decisions.
- [ ] 12. Test GG restart, Sonar disabled, endpoint plug/unplug, missing saved
      device, rapid device churn, and application restart scenarios. (Manual
      hardware verification - requires plugging/unplugging real devices and a
      running SteelSeries GG; see Verification checklist below.)
- [x] 13. Add the self-contained single-file publish profile and document use.

## Verification

- `dotnet build SoundController.sln`
- `dotnet test SoundController.sln`
- Launch SteelSeries GG with Sonar in Classic mode.
- Capture a known working configuration.
- Plug in and remove a PS5 controller and verify that Windows defaults and
  Sonar redirections return to the saved configuration without UI freezes.
- Restart SteelSeries GG and verify automatic reconnection.
- Restart the application and verify settings persist by endpoint ID.

## Publish

```powershell
dotnet publish src/SoundController/SoundController.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

## Initial Non-Goals

- Sonar Streamer-mode personal and stream mix support.
- SteelSeries Moments, Engine, or general GG settings.
- Multiple named profiles or automatic profile switching.
- Automatically selecting an unconfigured fallback when a locked endpoint is
  unavailable.
