using CodexBarWindows.Services;
using CodexBarWindows.ViewModels;

namespace CodexBarWindows.WpfSmokeTests;

public class SettingsViewModelSmokeTests
{
    [Fact]
    public async Task Save_updates_settings_and_startup_registration()
    {
        await StaTestRunner.RunAsync(() =>
        {
            using var paths = new TestAppDataPaths();
            var settings = new SettingsService(paths);
            var startup = new FakeStartupRegistration();
            var viewModel = new SettingsViewModel(settings, startup)
            {
                RunAtStartup = true,
                RefreshIntervalIndex = 4
            };

            viewModel.Save();

            Assert.Equal(15, settings.CurrentSettings.RefreshIntervalMinutes);
            Assert.Single(startup.Calls);
            Assert.True(startup.Calls[0].Enable);
        });
    }

    [Fact]
    public async Task Reset_defaults_reloads_default_values()
    {
        await StaTestRunner.RunAsync(() =>
        {
            using var paths = new TestAppDataPaths();
            var settings = new SettingsService(paths);
            var startup = new FakeStartupRegistration();
            var viewModel = new SettingsViewModel(settings, startup)
            {
                GlobalShortcut = "Ctrl+Shift+C"
            };

            viewModel.ResetDefaultsCommand.Execute(null);

            Assert.Equal(string.Empty, viewModel.GlobalShortcut);
        });
    }
}
