using System.IO;

namespace CodexBarWindows.Services;

public sealed class WindowsAppDataPaths : IAppDataPaths
{
    public string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CodexBarWindows");

    public string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");
    public string HistoryFilePath => Path.Combine(AppDataDirectory, "history.json");
    public string CrashLogFilePath => Path.Combine(AppDataDirectory, "crash.log");
}
