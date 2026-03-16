# Repository Guidelines

## Project Structure & Modules

- **Root**: C# / .NET 8 WPF application (`CodexBarWindows.csproj`). This is a Windows-native system tray app.
- `ViewModels/`: MVVM ViewModels (e.g., `TrayIconViewModel.cs`).
- `Views/`: WPF XAML windows (Settings, About).
- `Models/`: Data models and interfaces (e.g., `IProviderProbe`, `ProviderUsageStatus`).
- `Services/`: Core services — `SettingsService`, `IconGeneratorService`, `CliExecutionHelper`, `RefreshLoopService`.
- `Providers/`: Provider probe implementations (e.g., `CodexProvider.cs`). Each provider implements `IProviderProbe`.
- `Images/`: Static assets (icons, images).
- `macos_reference/`: Archived original macOS Swift source code for reference only. Do not modify.

## Build, Test, Run

- **Build**: `dotnet build` from the project root.
- **Run**: `dotnet run` or launch `bin\Debug\net9.0-windows\CodexBarWindows.exe`.
- **Kill stale instances**: `taskkill /F /IM CodexBarWindows.exe` before re-launching after a rebuild.
- **Dev loop**: `dotnet build && taskkill /F /IM CodexBarWindows.exe 2>nul; start bin\Debug\net9.0-windows\CodexBarWindows.exe`
- **Publish single-file**: `dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true`
- **Crash logs**: Written to `%APPDATA%\CodexBarWindows\crash.log`.
- **Settings file**: Stored at `%APPDATA%\CodexBarWindows\settings.json`.

## Coding Style & Naming

- C# conventions: PascalCase for public members, `_camelCase` for private fields. 4-space indent.
- Use `INotifyPropertyChanged` for ViewModels, `ICommand` / `RelayCommand` for actions.
- Use `async/await` for all I/O and CLI execution. Never block the UI thread.
- Favor dependency injection via `Microsoft.Extensions.DependencyInjection`.
- Keep provider logic isolated: each provider is its own class implementing `IProviderProbe`.

## Testing Guidelines

- Add unit tests under a `Tests/` project (xUnit or MSTest) when the test project is created.
- Always run `dotnet build` before handoff to ensure no compilation errors.
- After any code change, rebuild and re-launch the app to validate behavior.

## Commit & PR Guidelines

- Commit messages: short imperative clauses (e.g., "Add Claude provider", "Fix icon rendering"); keep commits scoped.
- PRs/patches should list summary, commands run, screenshots for UI changes.

## Agent Notes

- This is a **Windows-only C# / .NET 8 WPF** project. Do NOT use Swift, macOS, or Unix commands.
- Use NuGet for package management (`dotnet add package`). Do not add packages without confirmation.
- The app runs as a **system tray application** with no main window. `ShutdownMode` is `OnExplicitShutdown`.
- System tray icon is managed by `Hardcodet.NotifyIcon.Wpf` (`TaskbarIcon` in `App.xaml`).
- Dynamic icons are generated programmatically by `IconGeneratorService` using `System.Drawing.Common`.
- Keep provider data siloed: when rendering usage or account info for a provider, never display fields from a different provider.
- Cookie decryption on Windows uses DPAPI (`CryptUnprotectData`) instead of macOS Keychain.
- Browser cookie imports should default to Chrome/Edge. Override via settings when needed.
- The `macos_reference/` folder contains the original macOS codebase for architectural reference only.
