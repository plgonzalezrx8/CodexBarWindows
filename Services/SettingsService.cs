using System.Text.Json;
using System.IO;

namespace CodexBarWindows.Services;

public class SettingsService
{
    private readonly string _settingsFilePath;
    private AppSettings _currentSettings = new();

    public AppSettings CurrentSettings => _currentSettings;

    public SettingsService()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appDataPath, "CodexBarWindows");
        Directory.CreateDirectory(appFolder);
        _settingsFilePath = Path.Combine(appFolder, "settings.json");
        LoadSettings();
    }

    public void LoadSettings()
    {
        if (File.Exists(_settingsFilePath))
        {
            try
            {
                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    _currentSettings = settings;
                }
            }
            catch (Exception ex)
            {
                // Handle or log error
                System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
            }
        }
        else
        {
            SaveSettings(); // Create default file
        }
    }

    public void SaveSettings()
    {
        try
        {
            var json = JsonSerializer.Serialize(_currentSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }
}

public class AppSettings
{
    public Dictionary<string, bool> EnabledProviders { get; set; } = new()
    {
        { "codex", true },
        { "claude", true }
    };

    public int RefreshIntervalMinutes { get; set; } = 5;
    public bool RunAtStartup { get; set; } = false;
}
