using CodexBarWindows.ViewModels;
using CodexBarWindows.Views;
using Microsoft.Extensions.DependencyInjection;
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

    private static SettingsWindow? GetSettingsWindow(TrayIconViewModel tray)
    {
        var field = typeof(TrayIconViewModel).GetField("_settingsWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (SettingsWindow?)field?.GetValue(tray);
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
