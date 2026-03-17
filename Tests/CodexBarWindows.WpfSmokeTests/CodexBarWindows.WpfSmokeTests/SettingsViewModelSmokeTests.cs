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

    [Fact]
    public async Task Save_persists_provider_cookie_sources_and_display_mode()
    {
        await StaTestRunner.RunAsync(() =>
        {
            using var paths = new TestAppDataPaths();
            var settings = new SettingsService(paths);
            var startup = new FakeStartupRegistration();
            var viewModel = new SettingsViewModel(settings, startup);

            var claude = viewModel.Providers.Single(p => p.ProviderId == "claude");
            claude.IsEnabled = true;
            claude.CookieSourceIndex = 1;
            viewModel.DisplayModeIndex = 1;
            viewModel.ShowCredits = true;

            viewModel.Save();

            Assert.True(settings.CurrentSettings.EnabledProviders["claude"]);
            Assert.Equal("manual", settings.CurrentSettings.ProviderCookieSources["claude"]);
            Assert.Equal("Pace", settings.CurrentSettings.DisplayMode);
            Assert.True(settings.CurrentSettings.ShowCredits);
        });
    }

    [Fact]
    public async Task Reset_defaults_restores_default_provider_mappings_without_saving()
    {
        await StaTestRunner.RunAsync(() =>
        {
            using var paths = new TestAppDataPaths();
            var settings = new SettingsService(paths);
            settings.CurrentSettings.ProviderCookieSources["claude"] = "manual";
            settings.CurrentSettings.ShowCredits = true;
            var startup = new FakeStartupRegistration();
            var viewModel = new SettingsViewModel(settings, startup);

            viewModel.ResetDefaultsCommand.Execute(null);

            Assert.Equal(0, viewModel.Providers.Single(p => p.ProviderId == "claude").CookieSourceIndex);
            Assert.False(viewModel.ShowCredits);
            Assert.Equal("manual", settings.CurrentSettings.ProviderCookieSources["claude"]);
            Assert.True(settings.CurrentSettings.ShowCredits);
        });
    }
}
