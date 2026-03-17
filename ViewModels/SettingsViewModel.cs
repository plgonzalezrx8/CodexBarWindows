using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CodexBarWindows.Abstractions;
using CodexBarWindows.Services;

namespace CodexBarWindows.ViewModels;

/// <summary>
/// ViewModel for the Settings window. Exposes all settings as bindable properties
/// and commits changes to the SettingsService on Save.
/// </summary>
public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly SettingsService _settingsService;
    private readonly IStartupRegistration _startupRegistration;

    // ── Provider Definitions ────────────────────────────────────────
    public static readonly (string Id, string Name)[] AllProviders =
        ProviderDescriptorRegistry.All.Select(descriptor => (descriptor.Id, descriptor.Name)).ToArray();

    public static readonly string[] CookieSourceOptions = ["Auto", "Manual", "Off"];

    public static readonly string[] RefreshIntervalOptions = ["Manual", "1", "2", "5", "15"];

    public static readonly string[] DisplayModeOptions = ["Standard", "Pace"];

    // ── Collections ─────────────────────────────────────────────────
    public ObservableCollection<ProviderSettingsItem> Providers { get; } = [];

    // ── General Tab ─────────────────────────────────────────────────
    private int _refreshIntervalIndex;
    private bool _runAtStartup;
    private bool _enableStatusChecks;
    private bool _enableSessionNotifications;
    private bool _showCostSummary;

    public int RefreshIntervalIndex
    {
        get => _refreshIntervalIndex;
        set { _refreshIntervalIndex = value; OnPropertyChanged(); }
    }

    public bool RunAtStartup
    {
        get => _runAtStartup;
        set { _runAtStartup = value; OnPropertyChanged(); }
    }

    public bool EnableStatusChecks
    {
        get => _enableStatusChecks;
        set { _enableStatusChecks = value; OnPropertyChanged(); }
    }

    public bool EnableSessionNotifications
    {
        get => _enableSessionNotifications;
        set { _enableSessionNotifications = value; OnPropertyChanged(); }
    }

    public bool ShowCostSummary
    {
        get => _showCostSummary;
        set { _showCostSummary = value; OnPropertyChanged(); }
    }

    // ── Display Tab ─────────────────────────────────────────────────
    private bool _mergeIcons;
    private bool _switcherShowsIcons;
    private bool _showMostUsedProvider;
    private bool _menuBarShowsPercent;
    private int _displayModeIndex;
    private bool _showUsageAsUsed;
    private bool _showResetTimeAsClock;
    private bool _showCredits;
    private bool _showAllTokenAccounts;

    public bool MergeIcons
    {
        get => _mergeIcons;
        set { _mergeIcons = value; OnPropertyChanged(); }
    }

    public bool SwitcherShowsIcons
    {
        get => _switcherShowsIcons;
        set { _switcherShowsIcons = value; OnPropertyChanged(); }
    }

    public bool ShowMostUsedProvider
    {
        get => _showMostUsedProvider;
        set { _showMostUsedProvider = value; OnPropertyChanged(); }
    }

    public bool MenuBarShowsPercent
    {
        get => _menuBarShowsPercent;
        set { _menuBarShowsPercent = value; OnPropertyChanged(); }
    }

    public int DisplayModeIndex
    {
        get => _displayModeIndex;
        set { _displayModeIndex = value; OnPropertyChanged(); }
    }

    public bool ShowUsageAsUsed
    {
        get => _showUsageAsUsed;
        set { _showUsageAsUsed = value; OnPropertyChanged(); }
    }

    public bool ShowResetTimeAsClock
    {
        get => _showResetTimeAsClock;
        set { _showResetTimeAsClock = value; OnPropertyChanged(); }
    }

    public bool ShowCredits
    {
        get => _showCredits;
        set { _showCredits = value; OnPropertyChanged(); }
    }

    public bool ShowAllTokenAccounts
    {
        get => _showAllTokenAccounts;
        set { _showAllTokenAccounts = value; OnPropertyChanged(); }
    }

    // ── Advanced Tab ────────────────────────────────────────────────
    private string _globalShortcut = "";
    private bool _showDebugSettings;
    private bool _surpriseMe;
    private bool _hidePersonalInfo;
    private bool _disableCredentialAccess;

    public string GlobalShortcut
    {
        get => _globalShortcut;
        set { _globalShortcut = value; OnPropertyChanged(); }
    }

    public bool ShowDebugSettings
    {
        get => _showDebugSettings;
        set { _showDebugSettings = value; OnPropertyChanged(); }
    }

    public bool SurpriseMe
    {
        get => _surpriseMe;
        set { _surpriseMe = value; OnPropertyChanged(); }
    }

    public bool HidePersonalInfo
    {
        get => _hidePersonalInfo;
        set { _hidePersonalInfo = value; OnPropertyChanged(); }
    }

    public bool DisableCredentialAccess
    {
        get => _disableCredentialAccess;
        set { _disableCredentialAccess = value; OnPropertyChanged(); }
    }

    // ── About Tab ───────────────────────────────────────────────────
    public string AppVersion => System.Reflection.Assembly.GetExecutingAssembly()
        .GetName().Version?.ToString() ?? "1.0.0";

    public string SettingsFilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CodexBarWindows", "settings.json");

    // ── Commands ────────────────────────────────────────────────────
    public ICommand SaveCommand { get; }
    public ICommand ResetDefaultsCommand { get; }

    // ── Constructor ─────────────────────────────────────────────────

    public SettingsViewModel(SettingsService settingsService, IStartupRegistration startupRegistration)
    {
        _settingsService = settingsService;
        _startupRegistration = startupRegistration;
        SaveCommand = new RelayCommand(_ => Save());
        ResetDefaultsCommand = new RelayCommand(_ => ResetDefaults());
        LoadFromSettings();
    }

    // ── Load / Save ─────────────────────────────────────────────────

    private void LoadFromSettings()
    {
        var s = _settingsService.CurrentSettings;

        // Providers
        Providers.Clear();
        foreach (var (id, name) in AllProviders)
        {
            var isEnabled = s.EnabledProviders.GetValueOrDefault(id, false);
            var cookieSource = s.ProviderCookieSources.GetValueOrDefault(id, "auto");
            Providers.Add(new ProviderSettingsItem
            {
                ProviderId = id,
                ProviderName = name,
                IsEnabled = isEnabled,
                CookieSourceIndex = Array.IndexOf(CookieSourceOptions, cookieSource.Capitalize()) is >= 0 and var idx ? idx : 0
            });
        }

        // General
        RefreshIntervalIndex = s.RefreshIntervalMinutes switch
        {
            0 => 0,   // Manual
            1 => 1,
            2 => 2,
            5 => 3,
            15 => 4,
            _ => 3     // Default to 5m
        };
        RunAtStartup = s.RunAtStartup;
        EnableStatusChecks = s.EnableStatusChecks;
        EnableSessionNotifications = s.EnableSessionNotifications;
        ShowCostSummary = s.ShowCostSummary;

        // Display
        MergeIcons = s.MergeIcons;
        SwitcherShowsIcons = s.SwitcherShowsIcons;
        ShowMostUsedProvider = s.ShowMostUsedProvider;
        MenuBarShowsPercent = s.MenuBarShowsPercent;
        DisplayModeIndex = Array.IndexOf(DisplayModeOptions, s.DisplayMode) is >= 0 and var di ? di : 0;
        ShowUsageAsUsed = s.ShowUsageAsUsed;
        ShowResetTimeAsClock = s.ShowResetTimeAsClock;
        ShowCredits = s.ShowCredits;
        ShowAllTokenAccounts = s.ShowAllTokenAccounts;

        // Advanced
        GlobalShortcut = s.GlobalShortcut;
        ShowDebugSettings = s.ShowDebugSettings;
        SurpriseMe = s.SurpriseMe;
        HidePersonalInfo = s.HidePersonalInfo;
        DisableCredentialAccess = s.DisableCredentialAccess;
    }

    public void Save()
    {
        var s = _settingsService.CurrentSettings;

        // Providers
        foreach (var p in Providers)
        {
            s.EnabledProviders[p.ProviderId] = p.IsEnabled;
            var idx = Math.Clamp(p.CookieSourceIndex, 0, CookieSourceOptions.Length - 1);
            s.ProviderCookieSources[p.ProviderId] = CookieSourceOptions[idx].ToLowerInvariant();
        }

        // General
        s.RefreshIntervalMinutes = RefreshIntervalIndex switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            3 => 5,
            4 => 15,
            _ => 5
        };
        s.RunAtStartup = RunAtStartup;
        UpdateRegistryStartup(RunAtStartup);
        s.EnableStatusChecks = EnableStatusChecks;
        s.EnableSessionNotifications = EnableSessionNotifications;
        s.ShowCostSummary = ShowCostSummary;

        // Display
        s.MergeIcons = MergeIcons;
        s.SwitcherShowsIcons = SwitcherShowsIcons;
        s.ShowMostUsedProvider = ShowMostUsedProvider;
        s.MenuBarShowsPercent = MenuBarShowsPercent;
        s.DisplayMode = DisplayModeIndex >= 0 && DisplayModeIndex < DisplayModeOptions.Length
            ? DisplayModeOptions[DisplayModeIndex] : "Standard";
        s.ShowUsageAsUsed = ShowUsageAsUsed;
        s.ShowResetTimeAsClock = ShowResetTimeAsClock;
        s.ShowCredits = ShowCredits;
        s.ShowAllTokenAccounts = ShowAllTokenAccounts;

        // Advanced
        s.GlobalShortcut = GlobalShortcut;
        s.ShowDebugSettings = ShowDebugSettings;
        s.SurpriseMe = SurpriseMe;
        s.HidePersonalInfo = HidePersonalInfo;
        s.DisableCredentialAccess = DisableCredentialAccess;

        _settingsService.SaveSettings();
    }

    private void UpdateRegistryStartup(bool enable)
    {
        var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        _startupRegistration.SetRunAtStartup(enable, "CodexBarWindows", exePath);
    }

    private void ResetDefaults()
    {
        // Load default values into the view-model only; the live
        // SettingsService is left untouched until the user clicks Save.
        LoadFromDefaults(new AppSettings());
    }

    private void LoadFromDefaults(AppSettings s)
    {
        // Providers
        Providers.Clear();
        foreach (var (id, name) in AllProviders)
        {
            var isEnabled = s.EnabledProviders.GetValueOrDefault(id, false);
            var cookieSource = s.ProviderCookieSources.GetValueOrDefault(id, "auto");
            Providers.Add(new ProviderSettingsItem
            {
                ProviderId = id,
                ProviderName = name,
                IsEnabled = isEnabled,
                CookieSourceIndex = Array.IndexOf(CookieSourceOptions, cookieSource.Capitalize()) is >= 0 and var idx ? idx : 0
            });
        }

        // General
        RefreshIntervalIndex = s.RefreshIntervalMinutes switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            5 => 3,
            15 => 4,
            _ => 3
        };
        RunAtStartup = s.RunAtStartup;
        EnableStatusChecks = s.EnableStatusChecks;
        EnableSessionNotifications = s.EnableSessionNotifications;
        ShowCostSummary = s.ShowCostSummary;

        // Display
        MergeIcons = s.MergeIcons;
        SwitcherShowsIcons = s.SwitcherShowsIcons;
        ShowMostUsedProvider = s.ShowMostUsedProvider;
        MenuBarShowsPercent = s.MenuBarShowsPercent;
        DisplayModeIndex = Array.IndexOf(DisplayModeOptions, s.DisplayMode) is >= 0 and var di ? di : 0;
        ShowUsageAsUsed = s.ShowUsageAsUsed;
        ShowResetTimeAsClock = s.ShowResetTimeAsClock;
        ShowCredits = s.ShowCredits;
        ShowAllTokenAccounts = s.ShowAllTokenAccounts;

        // Advanced
        GlobalShortcut = s.GlobalShortcut;
        ShowDebugSettings = s.ShowDebugSettings;
        SurpriseMe = s.SurpriseMe;
        HidePersonalInfo = s.HidePersonalInfo;
        DisableCredentialAccess = s.DisableCredentialAccess;
    }

    // ── INotifyPropertyChanged ──────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ── Provider Settings Item ──────────────────────────────────────────

public class ProviderSettingsItem : INotifyPropertyChanged
{
    private bool _isEnabled;
    private int _cookieSourceIndex;

    public string ProviderId { get; set; } = "";
    public string ProviderName { get; set; } = "";

    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnPropertyChanged(); }
    }

    public int CookieSourceIndex
    {
        get => _cookieSourceIndex;
        set { _cookieSourceIndex = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ── String Extensions ───────────────────────────────────────────────

internal static class StringExtensions
{
    public static string Capitalize(this string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
}
