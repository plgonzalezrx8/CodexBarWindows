## Summary
This change introduces the first full testing scaffold for the Windows app and restructures the codebase so the most valuable logic can be tested without booting the WPF shell. The previous project layout kept provider logic, persistence, refresh orchestration, and UI-adjacent behavior inside the app project, which made automated testing brittle and limited. As a result, regressions in provider parsing, settings persistence, and refresh behavior were harder to catch before shipping.

The root issue was architectural coupling. Important logic depended directly on WPF, Windows registry access, credential storage, toast notifications, browser cookie import, and app-level state, so there was no clean place to build a fast test pyramid. This change extracts a new `CodexBarWindows.Core` library, defines explicit seams for environment, clock, command execution, credentials, notifications, startup registration, and tray presentation, and reconnects the WPF shell through adapter classes. That keeps existing runtime behavior intact while making the core behaviors testable in isolation.

From a user perspective, the effect is better reliability and a much safer development loop. Settings persistence, usage history, refresh/backoff logic, notification threshold behavior, provider parsing, and a small set of provider request flows are now covered by automated tests. The WPF shell also has smoke coverage for settings, host composition, and window creation. A Windows GitHub Actions workflow now restores, builds, runs the solution test suite, and uploads core coverage artifacts.

## What Changed
The repository now includes a solution file and three test projects: core unit tests, Windows integration tests, and WPF smoke tests. Provider/domain logic and non-UI services were moved into `CodexBarWindows.Core`, including settings persistence, usage history, provider models, provider implementations, notification logic, and refresh coordination. The app project now keeps Windows-specific adapters such as browser cookie import, credential manager access, tray presentation, registry startup registration, and toast notifications.

An `AppHostFactory` was added so the application host can be created consistently in production and test-friendly modes. `RefreshLoopService` now delegates refresh and backoff behavior to a core `RefreshCoordinator`, records history, forwards notifications, and then updates the tray through an injected presenter instead of reaching directly into WPF resources. `SettingsViewModel` now uses injected startup registration and the extracted settings service defaults rather than reflection-based mutation.

The test suite includes characterization-style coverage for provider parsing and representative integration flows for Codex, Cursor, Augment, and command execution. It also includes WPF smoke coverage for the settings window, settings view model, tray command wiring, and host creation. Shared test data fixtures were added to keep provider parsing tests grounded in realistic payloads.

## Validation
I verified the implementation locally with:

- `dotnet build CodexBarWindows.sln`
- `dotnet test CodexBarWindows.sln`
- `dotnet test Tests/CodexBarWindows.Core.Tests/CodexBarWindows.Core.Tests/CodexBarWindows.Core.Tests.csproj -p:CollectCoverage=true -p:CoverletOutputFormat=cobertura -p:CoverletOutput=TestResults/coverage/`

All build and test checks passed locally. Core coverage is being collected and uploaded in CI, but the current line coverage is still below the eventual 80 percent target, so this PR intentionally does not enforce that threshold yet.
