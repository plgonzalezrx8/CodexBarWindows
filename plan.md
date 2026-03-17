# CodexBarWindows Implementation Tasks

## Phase 1: Authentication Infrastructure
- [x] Implement Windows Credential Manager integration (replaces macOS Keychain)
- [x] Implement Chrome/Edge DPAPI browser cookie decryption service
- [x] Implement Firefox cookie reading support
- [x] Create Token/Cookie management service for caching headers

## Phase 2: Priority Providers
- [x] Implement [CodexProvider](file:///c:/Users/plgon/Downloads/CodexBarWindows/Providers/CodexProvider.cs#16-170) (CLI RPC + cookies)
- [x] Implement [ClaudeProvider](file:///c:/Users/plgon/Downloads/CodexBarWindows/Providers/ClaudeProvider.cs#26-35) (OAuth/cookies/CLI)
- [x] Implement [CursorProvider](file:///c:/Users/plgon/Downloads/CodexBarWindows/Providers/CursorProvider.cs#35-44) (Browser session cookies)
- [x] Implement [GeminiProvider](file:///c:/Users/plgon/Downloads/CodexBarWindows/Providers/GeminiProvider.cs#20-388) (OAuth)
- [x] Implement [AntigravityProvider](file:///c:/Users/plgon/Downloads/CodexBarWindows/Providers/AntigravityProvider.cs#34-39) (Local server probe)
- [x] Integrate Phase 1 auth services into these providers

## Phase 3: Settings UI Window
- [x] Create base [SettingsWindow.xaml](file:///c:/Users/plgon/Downloads/CodexBarWindows/Views/SettingsWindow.xaml) and [SettingsViewModel](file:///c:/Users/plgon/Downloads/CodexBarWindows/ViewModels/SettingsViewModel.cs#14-353)
- [x] Implement Providers Tab (Toggles, credentials, cookie source pickers)
- [x] Implement General Tab (Refresh cadence, startup, notifications)
- [x] Implement Display Tab (UI options, merge icons)
- [x] Implement Advanced/Debug/About Tabs

## Phase 4: Tray Menu & UI Enhancements
- [x] Implement Left-Click popup UI with usage cards
- [x] Implement Provider switcher in popup
- [x] Add per-provider tray icon styles (unique colors/shapes)
- [x] Implement 'Merge Icons' mode

## Phase 5: Additional Providers
- [x] Copilot (GitHub device flow + API)
- [x] OpenRouter (API token)
- [x] Kiro (CLI)
- [x] JetBrains AI (Local XML)
- [x] Augment, Amp, z.ai, Vertex AI, Kimi, etc.

## Phase 6: Background Services & Data
- [x] Implement parallel provider refresh
- [x] Add consecutive failure gate (backoff on failures)
- [x] Provider status page polling
- [x] Historical usage tracking + cost usage scan persistence

## Phase 7: System Integration (Windows-specific)
- [x] Start at login (Registry)
- [x] Global keyboard shortcut to open menu
- [x] Session quota notifications (Windows toast)
- [x] Auto-update mechanism
- [x] Verify single-file publish
