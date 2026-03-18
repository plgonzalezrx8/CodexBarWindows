using CodexBarWindows.ViewModels;
using CodexBarWindows.Views;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Windows;

namespace CodexBarWindows.WpfSmokeTests;

public class WindowAndHostSmokeTests
{
    [Fact]
    public async Task Settings_window_can_be_created_on_sta()
    {
        await StaTestRunner.RunAsync(() =>
        {
            StaTestRunner.EnsureApplication();
            using var paths = new TestAppDataPaths();
            var viewModel = new SettingsViewModel(new CodexBarWindows.Services.SettingsService(paths), new FakeStartupRegistration());
            var window = new SettingsWindow(viewModel);

            Assert.Same(viewModel, window.DataContext);
            window.Close();
        });
    }

    [Fact]
    public async Task Closing_settings_window_does_not_save_pending_changes()
    {
        await StaTestRunner.RunAsync(() =>
        {
            StaTestRunner.EnsureApplication();
            using var paths = new TestAppDataPaths();
            var settings = new CodexBarWindows.Services.SettingsService(paths);
            var viewModel = new SettingsViewModel(settings, new FakeStartupRegistration())
            {
                GlobalShortcut = "Ctrl+Shift+C"
            };
            var window = new SettingsWindow(viewModel);

            window.Close();

            Assert.Equal(string.Empty, settings.CurrentSettings.GlobalShortcut);
        });
    }

    [Fact]
    public async Task Tray_icon_command_opens_settings_window()
    {
        await StaTestRunner.RunAsync(() =>
        {
            StaTestRunner.EnsureApplication();
            using var paths = new TestAppDataPaths();
            var services = new ServiceCollection()
                .AddSingleton(new CodexBarWindows.Services.SettingsService(paths))
                .AddSingleton<CodexBarWindows.Abstractions.IStartupRegistration>(new FakeStartupRegistration())
                .AddTransient<SettingsViewModel>()
                .BuildServiceProvider();

            var tray = new TrayIconViewModel(services);
            tray.ShowSettingsCommand.Execute(null);

            var window = GetSettingsWindow(tray);
            Assert.NotNull(window);
            window!.Close();
        });
    }

    [Fact]
    public async Task Tray_icon_command_keeps_a_settings_window_available_after_reopen()
    {
        await StaTestRunner.RunAsync(() =>
        {
            StaTestRunner.EnsureApplication();
            using var paths = new TestAppDataPaths();
            var services = new ServiceCollection()
                .AddSingleton(new CodexBarWindows.Services.SettingsService(paths))
                .AddSingleton<CodexBarWindows.Abstractions.IStartupRegistration>(new FakeStartupRegistration())
                .AddTransient<SettingsViewModel>()
                .BuildServiceProvider();

            var tray = new TrayIconViewModel(services);
            tray.ShowSettingsCommand.Execute(null);
            var firstWindow = GetSettingsWindow(tray)!;
            firstWindow.WindowState = WindowState.Minimized;

            tray.ShowSettingsCommand.Execute(null);

            var reopenedWindow = GetSettingsWindow(tray);
            Assert.NotNull(reopenedWindow);
            Assert.Equal(WindowState.Normal, reopenedWindow!.WindowState);
            reopenedWindow.Close();
        });
    }

    [Fact]
    public async Task Closing_tray_settings_window_clears_cached_reference()
    {
        await StaTestRunner.RunAsync(() =>
        {
            StaTestRunner.EnsureApplication();
            using var paths = new TestAppDataPaths();
            var services = new ServiceCollection()
                .AddSingleton(new CodexBarWindows.Services.SettingsService(paths))
                .AddSingleton<CodexBarWindows.Abstractions.IStartupRegistration>(new FakeStartupRegistration())
                .AddTransient<SettingsViewModel>()
                .BuildServiceProvider();

            var tray = new TrayIconViewModel(services);
            tray.ShowSettingsCommand.Execute(null);
            var window = GetSettingsWindow(tray);

            Assert.NotNull(window);

            window!.Close();

            Assert.Null(GetSettingsWindow(tray));
        });
    }

    [Fact]
    public async Task Tray_icon_tooltip_raises_property_changed_only_when_value_changes()
    {
        await StaTestRunner.RunAsync(() =>
        {
            using var paths = new TestAppDataPaths();
            var services = new ServiceCollection()
                .AddSingleton(new CodexBarWindows.Services.SettingsService(paths))
                .AddSingleton<CodexBarWindows.Abstractions.IStartupRegistration>(new FakeStartupRegistration())
                .AddTransient<SettingsViewModel>()
                .BuildServiceProvider();
            var tray = new TrayIconViewModel(services);
            var propertyNames = new List<string?>();
            tray.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

            tray.TooltipText = "CodexBar";
            tray.TooltipText = "Updated";
            tray.TooltipText = "Updated";

            Assert.Single(propertyNames);
            Assert.Equal("TooltipText", propertyNames[0]);
        });
    }

    [Fact]
    public async Task Refresh_loop_refreshes_providers_and_updates_outputs()
    {
        using var paths = new TestAppDataPaths();
        var settings = new CodexBarWindows.Services.SettingsService(paths);
        var clock = new FakeClock();
        var notificationSink = new FakeNotificationSink();
        var notificationService = new CodexBarWindows.Services.NotificationService(settings, notificationSink, clock);
        var historyService = new CodexBarWindows.Services.UsageHistoryService(paths, clock);
        var trayPresenter = new FakeTrayPresenter();
        using var service = new CodexBarWindows.Services.RefreshLoopService(
            [
                new FakeProviderProbe("codex", "Codex", () => Task.FromResult(new CodexBarWindows.Models.ProviderUsageStatus
                {
                    ProviderId = "codex",
                    ProviderName = "Codex",
                    SessionProgress = 0.95,
                    TooltipText = "Codex tooltip"
                }))
            ],
            settings,
            new CodexBarWindows.Services.RefreshCoordinator(settings),
            historyService,
            notificationService,
            trayPresenter);

        await InvokeRefreshProvidersAsync(service, CancellationToken.None);

        var dayKey = clock.UtcNow.ToString("yyyy-MM-dd");
        Assert.Single(trayPresenter.Results);
        Assert.Equal("Codex tooltip", trayPresenter.Results[0].TooltipText);
        Assert.Single(notificationSink.Messages);
        Assert.True(historyService.History.ContainsKey(dayKey));
        Assert.True(historyService.History[dayKey].ContainsKey("codex"));
    }

    [Fact]
    public async Task Refresh_loop_wait_returns_true_when_manual_mode_receives_settings_change()
    {
        using var paths = new TestAppDataPaths();
        var settings = new CodexBarWindows.Services.SettingsService(paths);
        settings.CurrentSettings.RefreshIntervalMinutes = 0;
        var clock = new FakeClock();
        using var service = new CodexBarWindows.Services.RefreshLoopService(
            [],
            settings,
            new CodexBarWindows.Services.RefreshCoordinator(settings),
            new CodexBarWindows.Services.UsageHistoryService(paths, clock),
            new CodexBarWindows.Services.NotificationService(settings, new FakeNotificationSink(), clock),
            new FakeTrayPresenter());

        var resultTask = InvokeWaitForNextRefreshAsync(service, CancellationToken.None);
        settings.SaveSettings();

        Assert.True(await resultTask);
    }

    [Fact]
    public async Task Refresh_loop_wait_returns_true_when_timed_mode_receives_settings_change()
    {
        using var paths = new TestAppDataPaths();
        var settings = new CodexBarWindows.Services.SettingsService(paths);
        settings.CurrentSettings.RefreshIntervalMinutes = 1;
        var clock = new FakeClock();
        using var service = new CodexBarWindows.Services.RefreshLoopService(
            [],
            settings,
            new CodexBarWindows.Services.RefreshCoordinator(settings),
            new CodexBarWindows.Services.UsageHistoryService(paths, clock),
            new CodexBarWindows.Services.NotificationService(settings, new FakeNotificationSink(), clock),
            new FakeTrayPresenter());

        var resultTask = InvokeWaitForNextRefreshAsync(service, CancellationToken.None);
        settings.SaveSettings();

        Assert.True(await resultTask);
    }

    [Fact]
    public async Task Refresh_loop_wait_returns_false_when_manual_mode_is_canceled()
    {
        using var paths = new TestAppDataPaths();
        var settings = new CodexBarWindows.Services.SettingsService(paths);
        settings.CurrentSettings.RefreshIntervalMinutes = 0;
        var clock = new FakeClock();
        using var service = new CodexBarWindows.Services.RefreshLoopService(
            [],
            settings,
            new CodexBarWindows.Services.RefreshCoordinator(settings),
            new CodexBarWindows.Services.UsageHistoryService(paths, clock),
            new CodexBarWindows.Services.NotificationService(settings, new FakeNotificationSink(), clock),
            new FakeTrayPresenter());
        using var cancellationTokenSource = new CancellationTokenSource();

        var resultTask = InvokeWaitForNextRefreshAsync(service, cancellationTokenSource.Token);
        cancellationTokenSource.Cancel();

        Assert.False(await resultTask);
    }

    private static SettingsWindow? GetSettingsWindow(TrayIconViewModel tray)
    {
        var field = typeof(TrayIconViewModel).GetField("_settingsWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (SettingsWindow?)field?.GetValue(tray);
    }

    private static async Task<bool> InvokeWaitForNextRefreshAsync(CodexBarWindows.Services.RefreshLoopService service, CancellationToken cancellationToken)
    {
        var method = typeof(CodexBarWindows.Services.RefreshLoopService).GetMethod("WaitForNextRefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        var task = Assert.IsAssignableFrom<Task<bool>>(method?.Invoke(service, [cancellationToken]));
        return await task;
    }

    private static async Task InvokeRefreshProvidersAsync(CodexBarWindows.Services.RefreshLoopService service, CancellationToken cancellationToken)
    {
        var method = typeof(CodexBarWindows.Services.RefreshLoopService).GetMethod("RefreshProvidersAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        var task = Assert.IsAssignableFrom<Task>(method?.Invoke(service, [cancellationToken]));
        await task;
    }

    [Fact]
    public void Host_factory_creates_test_host_without_background_services()
    {
        using var host = AppHostFactory.Create(Array.Empty<string>(), new AppHostOptions(
            DisableBackgroundRefresh: true,
            DisableUpdateChecks: true,
            DisableHotkeys: true));

        Assert.NotNull(host.Services.GetRequiredService<CodexBarWindows.Services.SettingsService>());
        Assert.NotNull(host.Services.GetRequiredService<TrayIconViewModel>());
    }
}
