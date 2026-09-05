# SoundController Agent Guidance

This file defines how coding agents should work in this repository. It also
records project conventions so changes remain understandable to someone making
manual edits later. 

## Working Posture

- Read the target file and nearby implementation or test files before editing.
- Confirm library behavior from installed package APIs or authoritative source
  instead of guessing method names or event semantics.
- Prefer the smallest complete change that solves the requested problem.
- Do not perform unrelated cleanup or speculative refactoring.
- Keep one concern per change. If scope expands, record a follow-up in
  `TODO.md` rather than hiding extra work in the current task.
- Preserve user changes. Never revert or overwrite unrelated work in a dirty
  worktree.
- State assumptions when requirements or external API behavior are uncertain.
- Complete implementation, verification, and documentation together whenever
  feasible. Do not leave knowingly broken intermediate states.
- Keep `AGENTS.md`, `README.md`, and `TODO.md` current whenever a change makes
  them stale - new behavior, new conventions, or changed workflows. Treat the
  documentation update as part of that change, not a follow-up; when in doubt
  whether a change affects the docs, update them.
- Keep `CHANGELOG.md` current in the same change: add user-visible behavior
  under an "Unreleased" heading (or the version being prepared) using the
  Keep a Changelog format, and bump `<Version>` in `SoundController.csproj`
  when preparing a release.

## Design Principles

- Keep policy separate from integration code. `RestoreEngine` decides what
  should change; Sonar and Windows audio services perform the changes.
- Keep UI code thin. Connection, settings, and restore behavior belong in
  services that can be tested without WPF.
- Use dependency injection for long-lived services and external integrations.
- Match devices by stable endpoint ID. Display names are localized and may be
  duplicated or changed by driver updates.
- Restore only state that differs from the saved state. Avoid blind periodic
  writes that can cause event loops or audio interruptions.
- Restore passes queued before an apply must still respect the post-apply
  suppression window; defer them until it ends. A pass that re-reads state
  before the backend settled re-writes the correction just applied, and that
  echo churn has been observed to make Sonar's backend clear unrelated
  channels ("red, no device").
- When a saved endpoint is unavailable, report and wait for it. Never choose an
  arbitrary fallback unless that behavior is explicitly added to settings.
- Treat device notifications as hints to re-read state. USB and audio events
  can arrive more than once and in an unexpected order.

## Library-Specific Cautions

- SteelSeries GG's local Sonar service can change ports whenever GG restarts.
  Never cache the base URL; use `SonarClient` discovery and reconnection.
- This application initially targets Sonar Classic mode. Guard operations that
  depend on mode and provide a useful status if the mode is unsupported.
- Some Sonar change detection requires a configured polling interval. Keep the
  reason beside the configured value so it is not removed as apparent clutter.
- The Sonar API is unofficial and can change after a GG update. Keep its use
  behind `SonarService`, parse data tolerantly, and surface actionable errors.
- SteelSeries-NET-API 2.0.0's `Redirections.SetMicDeviceAsync` writes the
  Streamer-mode mic passthrough (`streamRedirections/mic/...`), not the
  Classic mic redirection (`classicRedirections/mic/...`) that
  `GetClassicRedirectionsAsync` reports. The write succeeds silently and
  nothing changes. Route every Classic channel - Mic included - through
  `SetClassicDeviceAsync`; reserve `SetMicDeviceAsync` for Streamer-mode
  work, which this build does not do.
- NAudio exposes endpoint enumeration and notifications, but changing the
  Windows default endpoint can require a small internal Core Audio policy
  interop layer. Keep that implementation isolated in `WindowsAudio` and cover
  role mapping with tests.
- H.NotifyIcon's WPF `TaskbarIcon` only creates the native icon inside its
  own `Loaded` handler, which never fires for an icon created in code outside
  a visual tree. Always call `ForceCreate(enablesEfficiencyMode: false)`
  explicitly; without it the process runs healthy but icon-less, and a second
  launch then reports "already running".
- Windows has Console, Multimedia, and Communications default roles. Do not
  assume setting one role updates the others; persist and restore intended
  roles explicitly.

## Error Handling and Lifecycle

- Catch specific exceptions before broad ones. Sonar failures should preserve
  the underlying exception for logs and show a concise degraded status in UI.
- Background event handlers must not crash the tray application. Log failures,
  maintain valid state, and allow later notifications or reconnects to retry.
- Honor `CancellationToken` in monitoring, debounce, reconnect, and shutdown
  paths.
- Dispose Sonar clients, NAudio enumerators, tray resources, cancellation token
  sources, and service providers in a deterministic shutdown order.
- Avoid `Thread.Sleep`, `.Result`, and `.Wait()`. Use asynchronous APIs and
  cancellation-aware delays.
- Marshal only the final view-state update to the WPF dispatcher. Never perform
  device or network I/O on the UI thread.
- Prevent duplicate restore operations with serialized or coalesced execution,
  not a collection of loosely coordinated boolean flags.

## Coding Conventions

### C# Style

The rules in this section are enforced mechanically: `.editorconfig` carries
them as code-style, naming, and analyzer rules and the build fails noisily
(`EnforceCodeStyleInBuild`, see `Directory.Build.props`) when they are
violated. Keep this section and `.editorconfig` in sync.

- Use file-scoped namespaces.
- Use four spaces, no tabs, and Allman braces.
- Use `PascalCase` for public members and types, `camelCase` for locals and
  parameters, and `_camelCase` for private fields.
- Use `var` when the assigned expression makes the type obvious; use an
  explicit type when it improves readability.
- Enable nullable reference types and resolve warnings rather than suppressing
  them without a documented reason.
- Prefer immutable records or init-only settings models where practical.
- Keep methods focused. Extract helpers when they represent a reusable concept
  or materially clarify complex logic, not merely to shorten a method.
- Avoid static service locators and mutable global state.

### Async and Events

- Suffix asynchronous methods with `Async` and return `Task` or `Task<T>`.
- Pass `CancellationToken` through operations that can block or be retried.
- Event handlers should enqueue or signal work and return quickly.
- Debounce related endpoint events before reading and restoring full state.
- Unsubscribe events during disposal to prevent leaks and callbacks after
  shutdown.

### Dependency Injection and Logging

- Construct the service provider once in `App.xaml.cs`.
- Register resource-owning services as singletons when their lifecycle matches
  the application.
- Use `Microsoft.Extensions.Logging`; do not use `Console.WriteLine` in
  application code.
- Never log secrets, full environment dumps, or excessive event noise.
- Include endpoint display names for diagnostics, but use endpoint IDs for all
  decisions.

### Settings

- Store settings beneath `%LOCALAPPDATA%\SoundController`, not beside the
  executable.
- Include a numeric `schemaVersion` in persisted settings.
- Write settings atomically through a temporary file and replace operation so
  power loss cannot leave truncated JSON.
- Validate settings when loading. Preserve the bad file for diagnosis instead
  of silently replacing it.
- Persist endpoint IDs and cache display names only for readable UI and logs.
- The settings window merges on save: slots the user did not touch keep their
  previously locked value (`LockedSlotMerge`), so a saved device missing from
  the freshly loaded device list can never silently wipe a lock. "(not
  locked)" only clears a slot the user deliberately picked. The merge runs
  per configuration: the window saves the profile selected in its
  Configuration section, and the other profile keeps its saved slots
  untouched.
- Two configurations exist ("headphones", "speakers", see `ProfileIds`), with
  an `ActiveProfileId` pointing at the one auto-restore enforces. Profile IDs
  are stable constants; profile names are display-only. The tray toggle and
  the settings window both switch through
  `RestoreCoordinator.ActivateProfileAsync`, which persists the switch and
  applies the new profile immediately.

### XAML and UI

- Follow MVVM for non-trivial state and actions; code-behind is limited to
  window-specific UI behavior.
- Name controls only when code-behind genuinely needs them.
- Do not add keyless (implicit) styles for `TextBlock` or `Separator` at
  application scope. Implicit application resources leak into the tray
  context menu popup (its headers render through internal TextBlocks) and
  made the menu text unreadable once. The tray menu opts into the keyed
  styles in `Themes/DarkGreen.xaml` explicitly instead.
- Move styles longer than a few setters into resources.
- Keep status wording actionable: connected, restoring, protected, saved
  device unavailable, Sonar disconnected, or unsupported mode.
- The settings window and tray menu must remain usable at common Windows DPI
  scales and with keyboard navigation.

## Comments and Manual Maintainability

Comments should help a future maintainer safely change integration behavior.
They should explain why a decision exists, not narrate syntax.

- Comment unofficial Sonar API assumptions, Windows Core Audio interop details,
  retry behavior, polling intervals, debounce durations, and role mappings.
- Place tunable constants near their use and explain the observed behavior that
  justifies the value.
- Use XML documentation for public abstractions when callers need lifecycle,
  units, threading, or side-effect information not expressed by the signature.
- Add a short file-level comment only when a file's responsibility or external
  constraint is not obvious from its primary type. Do not add repetitive
  boilerplate headers to every file.
- Use `TODO` comments only with concrete context, such as
  `TODO(StreamerMode): map personal and stream mix redirections separately`.
- Do not leave commented-out code. Version control retains removed code.
- Keep README instructions and `TODO.md` aligned with material behavior changes.
- When the project adopts or starts referencing a new open-source project,
  update the README Acknowledgments in the same change: credit it with its
  license and whether it is a dependency or reference-only.

Example of a useful comment:

```csharp
// GG can emit several endpoint events while Windows is still enumerating a USB
// device. Coalesce them before reading state so we do not restore a partial list.
await Task.Delay(DeviceChangeDebounce, cancellationToken);
```

Example of an unhelpful comment:

```csharp
// Wait before continuing.
await Task.Delay(500, cancellationToken);
```

## Testing and Verification

- Put pure restore comparison and decision logic under unit tests.
- Add regression tests whenever fixing a reproducible bug.
- Do not write tests that depend on the developer's named audio endpoints.
- Abstract Sonar and Windows endpoint access so tests can supply deterministic
  snapshots and failures.
- After code changes, run `dotnet build SoundController.sln` and
  `dotnet test SoundController.sln`.
- Keep `dotnet build` output free of analyzer and style warnings. Roslyn
  analyzers run at the "recommended" level and .editorconfig style rules are
  enforced in the build (see `Directory.Build.props`); fix violations in code,
  or demote a rule in `.editorconfig` only with a comment stating why. Verify
  formatting with `dotnet format SoundController.sln --verify-no-changes`
  (fix with `dotnet format SoundController.sln`).
- After large changes (new features, schema/migration work, restore-flow
  changes), produce a fresh single-file exe with
  `dotnet publish src/SoundController/SoundController.csproj -c Release
  -p:PublishProfile=FolderProfile` in the same change. Manual testing happens
  against the published exe, so a change is not deliverable without it.
- For integration changes, manually verify controller plug/unplug, missing
  preferred endpoints, SteelSeries GG restart, Sonar disabled, and application
  shutdown.
- If hardware-dependent verification cannot be completed, state exactly what
  remains unverified.

## Patterns to Avoid

- Hard-coded audio device display names or numeric enumeration indexes.
- Caching Sonar's local port or calling its internal HTTP API throughout UI
  code.
- Polling and rewriting every setting on a fixed timer when no state changed.
- Fire-and-forget tasks without observed exceptions and cancellation.
- Catching `Exception` and continuing without logging context.
- Blocking the WPF dispatcher with Sonar, filesystem, registry, or audio calls.
- Adding compatibility layers for unshipped behavior without a concrete need.
- Silently selecting a fallback endpoint when the user's locked device is
  unavailable.
