using System.Text.Json;
using System.IO;

namespace CodexBarWindows.Services;

public class SettingsService
{
    private readonly string _settingsFilePath;
    private AppSettings _currentSettings = new();

    public AppSettings CurrentSettings => _currentSettings;

    public event Action? SettingsChanged;

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
            SettingsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }
}

// ── Settings Model ──────────────────────────────────────────────────

public class AppSettings
{
    // ── Providers Tab ────────────────────────────────────────────
    public Dictionary<string, bool> EnabledProviders { get; set; } = new()
    {
        { "codex", true },
        { "claude", true },
        { "cursor", true },
        { "gemini", false },
        { "antigravity", false }
    };

    public Dictionary<string, string> ProviderCookieSources { get; set; } = new()
    {
        { "cursor", "auto" },
        { "claude", "auto" }
    };

    // ── General Tab ─────────────────────────────────────────────
    public int RefreshIntervalMinutes { get; set; } = 5;
    public bool RunAtStartup { get; set; } = false;
    public bool EnableStatusChecks { get; set; } = true;
    public bool EnableSessionNotifications { get; set; } = true;
    public bool ShowCostSummary { get; set; } = false;

    // ── Display Tab ─────────────────────────────────────────────
    public bool MergeIcons { get; set; } = false;
    public bool SwitcherShowsIcons { get; set; } = true;
    public bool ShowMostUsedProvider { get; set; } = false;
    public bool MenuBarShowsPercent { get; set; } = false;
    public string DisplayMode { get; set; } = "Standard"; // Standard | Pace
    public bool ShowUsageAsUsed { get; set; } = false;     // false = show as "remaining"
    public bool ShowResetTimeAsClock { get; set; } = false;
    public bool ShowCredits { get; set; } = false;
    public bool ShowAllTokenAccounts { get; set; } = false;
    public List<string> OverviewProviders { get; set; } = ["codex", "claude", "cursor"];

    // ── Advanced Tab ────────────────────────────────────────────
    public string GlobalShortcut { get; set; } = "";
    public bool ShowDebugSettings { get; set; } = false;
    public bool SurpriseMe { get; set; } = false;
    public bool HidePersonalInfo { get; set; } = false;
    public bool DisableCredentialAccess { get; set; } = false;
}
