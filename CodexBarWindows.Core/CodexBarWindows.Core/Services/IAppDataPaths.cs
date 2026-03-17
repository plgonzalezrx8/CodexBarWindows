namespace CodexBarWindows.Services;

public interface IAppDataPaths
{
    string AppDataDirectory { get; }
    string SettingsFilePath { get; }
    string HistoryFilePath { get; }
    string CrashLogFilePath { get; }
}
