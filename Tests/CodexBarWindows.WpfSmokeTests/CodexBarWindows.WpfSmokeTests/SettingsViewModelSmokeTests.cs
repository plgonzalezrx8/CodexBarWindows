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

    [Fact]
    public async Task Loading_settings_uses_provider_descriptor_default_when_key_is_missing()
    {
        await StaTestRunner.RunAsync(() =>
        {
            using var paths = new TestAppDataPaths();
            var settings = new SettingsService(paths);
            settings.CurrentSettings.EnabledProviders.Remove("codex");
            var startup = new FakeStartupRegistration();

            var viewModel = new SettingsViewModel(settings, startup);

            Assert.True(viewModel.Providers.Single(p => p.ProviderId == "codex").IsEnabled);
        });
    }

    [Fact]
    public async Task Loading_unknown_values_falls_back_to_supported_defaults()
    {
        await StaTestRunner.RunAsync(() =>
        {
            using var paths = new TestAppDataPaths();
            var settings = new SettingsService(paths);
            settings.CurrentSettings.RefreshIntervalMinutes = 99;
            settings.CurrentSettings.DisplayMode = "Unexpected";
            settings.CurrentSettings.ProviderCookieSources["claude"] = "mystery";

            var viewModel = new SettingsViewModel(settings, new FakeStartupRegistration());

            Assert.Equal(3, viewModel.RefreshIntervalIndex);
            Assert.Equal(0, viewModel.DisplayModeIndex);
            Assert.Equal(0, viewModel.Providers.Single(p => p.ProviderId == "claude").CookieSourceIndex);
        });
    }

    [Fact]
    public async Task Save_clamps_invalid_indices_to_supported_values()
    {
        await StaTestRunner.RunAsync(() =>
        {
            using var paths = new TestAppDataPaths();
            var settings = new SettingsService(paths);
            var viewModel = new SettingsViewModel(settings, new FakeStartupRegistration());
            var codex = viewModel.Providers.Single(p => p.ProviderId == "codex");
            var claude = viewModel.Providers.Single(p => p.ProviderId == "claude");

            codex.CookieSourceIndex = -10;
            claude.CookieSourceIndex = 99;
            viewModel.RefreshIntervalIndex = 99;
            viewModel.DisplayModeIndex = -1;

            viewModel.Save();

            Assert.Equal(5, settings.CurrentSettings.RefreshIntervalMinutes);
            Assert.Equal("Standard", settings.CurrentSettings.DisplayMode);
            Assert.Equal("auto", settings.CurrentSettings.ProviderCookieSources["codex"]);
            Assert.Equal("off", settings.CurrentSettings.ProviderCookieSources["claude"]);
        });
    }

    [Fact]
    public async Task Provider_settings_item_raises_property_changed_for_each_mutation()
    {
        await StaTestRunner.RunAsync(() =>
        {
            var item = new ProviderSettingsItem();
            var propertyNames = new List<string?>();
            item.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

            item.IsEnabled = true;
            item.CookieSourceIndex = 2;

            Assert.Collection(
                propertyNames,
                propertyName => Assert.Equal("IsEnabled", propertyName),
                propertyName => Assert.Equal("CookieSourceIndex", propertyName));
        });
    }
}
