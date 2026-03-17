using CodexBarWindows.Abstractions;
using Microsoft.Win32;

namespace CodexBarWindows.Services;

public sealed class RegistryStartupRegistration : IStartupRegistration
{
    public void SetRunAtStartup(bool enable, string appName, string? executablePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null)
            {
                return;
            }

            if (enable && !string.IsNullOrWhiteSpace(executablePath))
            {
                key.SetValue(appName, $"\"{executablePath}\"");
                return;
            }

            key.DeleteValue(appName, false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to set startup registry key: {ex.Message}");
        }
    }
}
