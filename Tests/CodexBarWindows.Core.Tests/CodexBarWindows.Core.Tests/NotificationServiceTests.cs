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
}
