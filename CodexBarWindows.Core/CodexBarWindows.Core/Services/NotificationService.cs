using CodexBarWindows.Abstractions;
using CodexBarWindows.Models;

namespace CodexBarWindows.Services;

public class NotificationService
{
    private readonly SettingsService _settings;
    private readonly INotificationSink _notificationSink;
    private readonly IClock _clock;
    private readonly Dictionary<string, (bool Notified90, bool Notified100)> _notifiedState = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly _lastResetDate;

    public NotificationService(SettingsService settings, INotificationSink notificationSink, IClock clock)
    {
        _settings = settings;
        _notificationSink = notificationSink;
        _clock = clock;
        _lastResetDate = DateOnly.FromDateTime(clock.LocalNow);
    }

    public void ProcessStatuses(IEnumerable<ProviderUsageStatus> statuses)
    {
        if (!_settings.CurrentSettings.EnableSessionNotifications)
        {
            return;
        }

        var currentDate = DateOnly.FromDateTime(_clock.LocalNow);
        if (currentDate != _lastResetDate)
        {
            _notifiedState.Clear();
            _lastResetDate = currentDate;
        }

        foreach (var status in statuses)
        {
            if (status.IsError)
            {
                continue;
            }

            if (!_notifiedState.TryGetValue(status.ProviderId, out var state))
            {
                state = (false, false);
            }

            if (status.SessionProgress >= 1.0 && !state.Notified100)
            {
                _notificationSink.Show($"CodexBar: {status.ProviderName}", "Session quota exhausted (100% used).");
                state.Notified100 = true;
                state.Notified90 = true;
            }
            else if (status.SessionProgress >= 0.90 && status.SessionProgress < 1.0 && !state.Notified90)
            {
                _notificationSink.Show($"CodexBar: {status.ProviderName}", "Session quota is running low (over 90% used).");
                state.Notified90 = true;
            }

            _notifiedState[status.ProviderId] = state;
        }
    }
}
