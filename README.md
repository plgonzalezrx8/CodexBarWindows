# CodexBar Windows

> May your tokens never run out

Windows native (WPF / .NET 8) system tray app that keeps your AI provider usage limits (Codex, Claude, etc.) visible and shows when each window resets. Inspired by the original macOS CodexBar, rebuilt for Windows.

## Architecture

This project is a native Windows rewrite using:

- **C# / .NET 8** with **WPF**
- **System Tray Icon:** Powered by `Hardcodet.NotifyIcon.Wpf`
- **Dependency Injection:** `Microsoft.Extensions.Hosting`
- **Dynamic Icons:** Generated via `System.Drawing.Common` to show live progress bars (Session/Weekly)

*(Note: The original macOS Swift source code is archived in the `macos_reference` folder for reference.)*

## Features (In Progress)

- Background polling of CLI tools (e.g. `codex`, `claude`)
- Dynamic Taskbar icon generation (Progress bars for limits)
- Secure credential management via Windows APIs
- Local cost-usage scan
- Settings UI

## Getting Started (Dev)

1. Install the .NET 8.0 SDK for Windows.
2. Clone this repository.
3. Build the project:

   ```cmd
   dotnet build
   ```

4. Run the project:

   ```cmd
   dotnet run
   ```

## Original Credits

Inspired by and ported from the original macOS [CodexBar](https://github.com/steipete/CodexBar) by Peter Steinberger (steipete).
