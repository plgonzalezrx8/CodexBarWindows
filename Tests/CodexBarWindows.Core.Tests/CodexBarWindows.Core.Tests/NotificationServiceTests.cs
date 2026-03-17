using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Core.Tests;

public class NotificationServiceTests
{
    [Fact]
    public void Sends_each_threshold_once_per_day_and_resets_next_day()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var clock = new FakeClock();
        var sink = new FakeNotificationSink();
        var service = new NotificationService(settings, sink, clock);

        var warning = new ProviderUsageStatus { ProviderId = "codex", ProviderName = "Codex", SessionProgress = 0.95 };
        service.ProcessStatuses([warning, warning]);

        Assert.Single(sink.Messages);

        clock.LocalNow = clock.LocalNow.AddDays(1);
        service.ProcessStatuses([warning]);

        Assert.Equal(2, sink.Messages.Count);
    }

    [Fact]
    public void Sends_hundred_percent_notification_only_once_and_skips_errors()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var clock = new FakeClock();
        var sink = new FakeNotificationSink();
        var service = new NotificationService(settings, sink, clock);

        service.ProcessStatuses(
        [
            new ProviderUsageStatus { ProviderId = "codex", ProviderName = "Codex", SessionProgress = 1.0 },
            new ProviderUsageStatus { ProviderId = "claude", ProviderName = "Claude", SessionProgress = 1.0, IsError = true }
        ]);
        service.ProcessStatuses(
        [
            new ProviderUsageStatus { ProviderId = "codex", ProviderName = "Codex", SessionProgress = 1.0 }
        ]);

        Assert.Single(sink.Messages);
        Assert.Contains("exhausted", sink.Messages[0].Message);
    }

    [Fact]
    public void Does_not_send_notifications_when_disabled()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        settings.CurrentSettings.EnableSessionNotifications = false;
        var sink = new FakeNotificationSink();
        var service = new NotificationService(settings, sink, new FakeClock());

        service.ProcessStatuses([new ProviderUsageStatus { ProviderId = "codex", ProviderName = "Codex", SessionProgress = 0.95 }]);

        Assert.Empty(sink.Messages);
    }
}
