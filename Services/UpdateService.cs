using System.Net.Http;
using System.Text.Json;
using Microsoft.Toolkit.Uwp.Notifications;

namespace CodexBarWindows.Services;

/// <summary>
/// Checks GitHub for new releases and notifies the user via Windows Toast
/// if a newer version is available.
/// </summary>
public class UpdateService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "User-Agent", "CodexBarWindows/1.0" } }
    };

    public async Task CheckForUpdatesAsync()
    {
        try
        {
            // Update to actual repo name if changed
            var url = "https://api.github.com/repos/plgonzalezrx8/CodexBarWindows/releases/latest";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            
            var tagName = doc.RootElement.GetProperty("tag_name").GetString();
            
            // Get current version (e.g., 1.0.0)
            var currentVersionNum = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            var currentVersion = "v" + currentVersionNum;

            if (tagName != null && string.Compare(tagName, currentVersion, StringComparison.OrdinalIgnoreCase) > 0)
            {
                new ToastContentBuilder()
                    .AddArgument("action", "update")
                    .AddText("CodexBar Update Available")
                    .AddText($"New version {tagName} is available. You are running {currentVersion}.")
                    .Show();
            }
        }
        catch
        {
            // Fail silently if no internet or repo doesn't exist yet
        }
    }
}
