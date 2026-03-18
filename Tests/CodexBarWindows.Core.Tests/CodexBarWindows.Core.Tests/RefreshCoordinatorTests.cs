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

    [Fact]
    public async Task Uses_single_provider_tooltip_when_only_one_status_is_returned()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var coordinator = new RefreshCoordinator(settings);
        var provider = new FakeProvider(
            "codex",
            "Codex",
            () => Task.FromResult(new ProviderUsageStatus
            {
                ProviderId = "codex",
                ProviderName = "Codex",
                TooltipText = "Codex tooltip"
            }));

        var result = await coordinator.RefreshAsync([provider], CancellationToken.None);

        Assert.Equal("Codex tooltip", result.TooltipText);
    }

    [Fact]
    public async Task Mixed_statuses_report_active_count()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        settings.CurrentSettings.EnabledProviders["claude"] = true;
        var coordinator = new RefreshCoordinator(settings);
        var providers = new IProviderProbe[]
        {
            new FakeProvider("codex", "Codex", () => Task.FromResult(new ProviderUsageStatus
            {
                ProviderId = "codex",
                ProviderName = "Codex",
                TooltipText = "Codex"
            })),
            new FakeProvider("claude", "Claude", () => Task.FromResult(new ProviderUsageStatus
            {
                ProviderId = "claude",
                ProviderName = "Claude",
                IsError = true,
                TooltipText = "Claude: Error"
            }))
        };

        var result = await coordinator.RefreshAsync(providers, CancellationToken.None);

        Assert.Equal("CodexBar (1 active)", result.TooltipText);
    }

    [Fact]
    public async Task Successful_refresh_clears_backoff_state()
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
                TooltipText = "Recovered"
            }),
            () => Task.FromResult(new ProviderUsageStatus
            {
                ProviderId = "codex",
                ProviderName = "Codex",
                TooltipText = "Recovered"
            }));

        var first = await coordinator.RefreshAsync([provider], CancellationToken.None);
        var second = await coordinator.RefreshAsync([provider], CancellationToken.None);
        var third = await coordinator.RefreshAsync([provider], CancellationToken.None);

        Assert.True(first.Statuses.Single().IsError);
        Assert.Equal("Recovered", third.TooltipText);
        Assert.Equal("Recovered", third.Statuses.Single().TooltipText);
        Assert.False(second.Statuses.Single().TooltipText.Contains("Recovered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_change_resets_failure_tracking_so_provider_is_retried()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var coordinator = new RefreshCoordinator(settings);
        var provider = new FakeProvider(
            "codex",
            "Codex",
            () => throw new InvalidOperationException("boom"),
            // Second entry won't be consumed because call 2 is paused (skip cycle)
            () => Task.FromResult(new ProviderUsageStatus
            {
                ProviderId = "codex",
                ProviderName = "Codex",
                TooltipText = "Recovered after reset"
            }));

        // First call fails, setting skip cycles
        var first = await coordinator.RefreshAsync([provider], CancellationToken.None);
        Assert.True(first.Statuses.Single().IsError);

        // Without reset, second call would be paused (provider lambda not invoked)
        var paused = await coordinator.RefreshAsync([provider], CancellationToken.None);
        Assert.Contains("Paused", paused.Statuses.Single().TooltipText);

        // Simulate settings save — should clear skip cycles
        settings.SaveSettings();

        // Now the provider should be retried immediately (consumes second entry)
        var afterReset = await coordinator.RefreshAsync([provider], CancellationToken.None);
        Assert.False(afterReset.Statuses.Single().IsError);
        Assert.Equal("Recovered after reset", afterReset.Statuses.Single().TooltipText);
    }
}
