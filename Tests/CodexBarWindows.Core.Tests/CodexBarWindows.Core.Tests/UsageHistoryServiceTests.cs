using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Core.Tests;

public class UsageHistoryServiceTests
{
    [Fact]
    public void Records_only_non_error_statuses_and_persists_them()
    {
        using var paths = new TestAppDataPaths();
        var clock = new FakeClock();
        var service = new UsageHistoryService(paths, clock);

        service.RecordSnapshot(
        [
            new ProviderUsageStatus { ProviderId = "codex", ProviderName = "Codex", SessionProgress = 0.4, TooltipText = "Codex" },
            new ProviderUsageStatus { ProviderId = "claude", ProviderName = "Claude", IsError = true, TooltipText = "Claude error" }
        ]);

        var reloaded = new UsageHistoryService(paths, clock);
        var dayKey = clock.UtcNow.ToString("yyyy-MM-dd");

        Assert.True(reloaded.History.ContainsKey(dayKey));
        Assert.True(reloaded.History[dayKey].ContainsKey("codex"));
        Assert.False(reloaded.History[dayKey].ContainsKey("claude"));
    }

    [Fact]
    public void Does_not_write_history_when_all_statuses_are_errors()
    {
        using var paths = new TestAppDataPaths();
        var service = new UsageHistoryService(paths, new FakeClock());

        service.RecordSnapshot(
        [
            new ProviderUsageStatus { ProviderId = "codex", ProviderName = "Codex", IsError = true }
        ]);

        Assert.False(File.Exists(paths.HistoryFilePath));
        Assert.Single(service.History);
        Assert.Empty(service.History.Values.Single());
    }

    [Fact]
    public void Handles_invalid_history_file_by_starting_fresh()
    {
        using var paths = new TestAppDataPaths();
        Directory.CreateDirectory(paths.AppDataDirectory);
        File.WriteAllText(paths.HistoryFilePath, "{not json");

        var service = new UsageHistoryService(paths, new FakeClock());

        Assert.Empty(service.History);
    }
}
