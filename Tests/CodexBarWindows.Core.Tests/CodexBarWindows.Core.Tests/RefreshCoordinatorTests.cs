using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Core.Tests;

public class RefreshCoordinatorTests
{
    [Fact]
    public async Task Returns_empty_tooltip_when_no_providers_are_enabled()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        settings.CurrentSettings.EnabledProviders["codex"] = false;
        var coordinator = new RefreshCoordinator(settings);
        var provider = new FakeProvider("codex", "Codex");

        var result = await coordinator.RefreshAsync([provider], CancellationToken.None);

        Assert.Empty(result.Statuses);
        Assert.Equal("CodexBar (No providers enabled)", result.TooltipText);
    }

    [Fact]
    public async Task Applies_backoff_after_failures()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var coordinator = new RefreshCoordinator(settings);
        var provider = new FakeProvider(
            "codex",
            "Codex",
            () => throw new InvalidOperationException("boom"),
            () => Task.FromResult(new ProviderUsageStatus
            {
                ProviderId = "codex",
                ProviderName = "Codex",
                TooltipText = "Codex"
            }));

        var first = await coordinator.RefreshAsync([provider], CancellationToken.None);
        var second = await coordinator.RefreshAsync([provider], CancellationToken.None);

        Assert.True(first.Statuses.Single().IsError);
        Assert.Contains("Paused", second.Statuses.Single().TooltipText);
    }
}
