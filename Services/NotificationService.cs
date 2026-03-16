using Microsoft.Toolkit.Uwp.Notifications;
using CodexBarWindows.Models;

namespace CodexBarWindows.Services;

public class NotificationService
{
    private readonly SettingsService _settings;
    
    // Track which thresholds we've already notified the user about for each provider today
    private readonly Dictionary<string, (bool Notified90, bool Notified100)> _notifiedState = new();
    private DateTime _lastResetDate = DateTime.Today;

    public NotificationService(SettingsService settings)
    {
        _settings = settings;
    }

    public void ProcessStatuses(IEnumerable<ProviderUsageStatus> statuses)
    {
        if (!_settings.CurrentSettings.EnableSessionNotifications) return;

        // Reset tracking on a new day
        if (DateTime.Today != _lastResetDate)
        {
            _notifiedState.Clear();
            _lastResetDate = DateTime.Today;
        }

        foreach (var status in statuses)
        {
            if (status.IsError) continue;

            if (!_notifiedState.TryGetValue(status.ProviderId, out var state))
            {
                state = (false, false);
            }

            // Exceeded 100%
            if (status.SessionProgress >= 1.0 && !state.Notified100)
            {
                ShowToast(status.ProviderName, "Session quota exhausted (100% used).");
                state.Notified100 = true;
                state.Notified90 = true; // Skip 90% if we jumped straight to 100%
            }
            // Warning 90%
            else if (status.SessionProgress >= 0.90 && status.SessionProgress < 1.0 && !state.Notified90)
            {
                ShowToast(status.ProviderName, "Session quota is running low (over 90% used).");
                state.Notified90 = true;
            }

            _notifiedState[status.ProviderId] = state;
        }
    }

    private static void ShowToast(string providerName, string message)
    {
        new ToastContentBuilder()
            .AddArgument("action", "viewDetails")
            .AddText($"CodexBar: {providerName}")
            .AddText(message)
            .Show();
    }
}
