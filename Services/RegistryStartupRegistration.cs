using CodexBarWindows.Abstractions;
using Microsoft.Win32;

namespace CodexBarWindows.Services;

public sealed class RegistryStartupRegistration : IStartupRegistration
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public void SetRunAtStartup(bool enable, string appName, string? executablePath = null)
    {
        try
        {
            if (enable)
            {
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    return;
                }

                // Ensure the Run key exists on fresh user profiles before writing.
                using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                runKey?.SetValue(appName, $"\"{executablePath}\"");
                return;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(appName, false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to set startup registry key: {ex.Message}");
        }
    }
}
