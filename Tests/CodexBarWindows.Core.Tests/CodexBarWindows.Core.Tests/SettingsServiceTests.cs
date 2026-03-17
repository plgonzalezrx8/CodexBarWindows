using CodexBarWindows.Services;

namespace CodexBarWindows.Core.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void Creates_default_settings_file_on_first_load()
    {
        using var paths = new TestAppDataPaths();

        var service = new SettingsService(paths);

        Assert.True(File.Exists(paths.SettingsFilePath));
        Assert.Equal(5, service.CurrentSettings.RefreshIntervalMinutes);
        Assert.True(service.CurrentSettings.EnabledProviders["codex"]);
    }

    [Fact]
    public void Persists_changes_across_instances()
    {
        using var paths = new TestAppDataPaths();
        var first = new SettingsService(paths);
        first.CurrentSettings.RefreshIntervalMinutes = 15;
        first.CurrentSettings.EnabledProviders["gemini"] = true;
        first.SaveSettings();

        var second = new SettingsService(paths);

        Assert.Equal(15, second.CurrentSettings.RefreshIntervalMinutes);
        Assert.True(second.CurrentSettings.EnabledProviders["gemini"]);
    }

    [Fact]
    public void Reset_to_defaults_restores_default_values()
    {
        using var paths = new TestAppDataPaths();
        var service = new SettingsService(paths);
        service.CurrentSettings.GlobalShortcut = "Ctrl+Shift+C";

        service.ResetToDefaults();

        Assert.Equal(string.Empty, service.CurrentSettings.GlobalShortcut);
        Assert.Equal(5, service.CurrentSettings.RefreshIntervalMinutes);
    }

    [Fact]
    public void Falls_back_to_defaults_when_settings_json_is_invalid()
    {
        using var paths = new TestAppDataPaths();
        Directory.CreateDirectory(paths.AppDataDirectory);
        File.WriteAllText(paths.SettingsFilePath, "{invalid json");

        var service = new SettingsService(paths);

        Assert.Equal(5, service.CurrentSettings.RefreshIntervalMinutes);
        Assert.True(service.CurrentSettings.EnabledProviders["codex"]);
    }

    [Fact]
    public void Save_settings_raises_change_event()
    {
        using var paths = new TestAppDataPaths();
        var service = new SettingsService(paths);
        var raised = 0;
        service.SettingsChanged += () => raised++;

        service.SaveSettings();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Reset_to_defaults_with_save_persists_new_values()
    {
        using var paths = new TestAppDataPaths();
        var service = new SettingsService(paths);
        service.CurrentSettings.GlobalShortcut = "Ctrl+Shift+C";
        service.CurrentSettings.RefreshIntervalMinutes = 15;

        service.ResetToDefaults(save: true);

        var reloaded = new SettingsService(paths);
        Assert.Equal(string.Empty, reloaded.CurrentSettings.GlobalShortcut);
        Assert.Equal(5, reloaded.CurrentSettings.RefreshIntervalMinutes);
    }
}
