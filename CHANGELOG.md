# Changelog

All notable changes to SoundController are documented here. The format is
based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions
follow [SemVer](https://semver.org/) while the project is pre-1.0.

## [0.1.1] - 2026-09-01

### Added

- Lint tooling built into the SDK: Roslyn analyzers at the "recommended"
  analysis level, `.editorconfig` enforcing the project style (file-scoped
  namespaces, naming, layout) in every build, and
  `dotnet format --verify-no-changes` as the formatting gate. Baseline is
  clean; existing analyzer findings were fixed in code.
- Two named configurations, **Headphones** and **Speakers**, with an
  active-profile pointer; switch between them with the tray menu toggle or
  the settings window's **Make active** button - the switch is persisted and
  applied immediately, and auto-restore keeps enforcing the active one.
- Settings schema v1 -> v2 migration: the previously captured single state
  becomes the Headphones configuration and stays active, so upgrade-time
  enforcement is unchanged; the Speakers configuration starts empty.
- The settings window's **Configuration** section selects which
  configuration the device lists edit; slot merges (`LockedSlotMerge`) run
  per configuration.
- "Capture current" now targets the active configuration; an empty active
  configuration reports "No saved setup" instead of a misleading "devices
  match".
- Toggles (auto-restore, Start with Windows) now apply immediately in the
  settings window - no Save/Apply press needed - and stay in sync with the
  tray menu in both directions (changes are broadcast through a
  `SettingsService.Saved` notification; the tray re-reads the registry each
  time its menu opens).
- `CHANGELOG.md` and a versioned exe (`0.1.1`).

### Fixed

- Toggling Start with Windows or auto-restore in one surface (tray or
  settings window) is no longer silently reverted by pressing Save in the
  other surface with a stale checkbox; Save only concerns the device lists.

## [0.1.0] - 2026-08

### Added

- Initial feature set: keeps SteelSeries Sonar (Classic mode) redirections
  and Windows default devices locked.
- Debounced change monitoring with a post-apply suppression window; only
  values that drift from the saved state are rewritten.
- Unavailable saved devices are reported and waited for - never replaced by
  a fallback.
- Tray application: status dot, capture/apply actions, auto-restore and
  autostart toggles, dark themed menu.
- Settings window with per-slot device pickers and merge-on-save semantics.
- Versioned JSON settings persisted atomically under
  `%LOCALAPPDATA%\SoundController` with schema validation and corrupt-file
  preservation.
- Self-contained single-file publish (`publish/SoundController.exe`).
