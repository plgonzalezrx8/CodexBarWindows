using System.Text.Json;
using CodexBarWindows.Models;

namespace CodexBarWindows.Services;

/// <summary>
/// Service to persist provider usage snapshots to disk for historical tracking.
/// Provides a foundation for cost calculation and historical graphs.
/// </summary>
public class UsageHistoryService
{
    private readonly IAppDataPaths _appDataPaths;
    private readonly CodexBarWindows.Abstractions.IClock _clock;
    private Dictionary<string, Dictionary<string, ProviderUsageStatus>> _historyCache;

    public IReadOnlyDictionary<string, Dictionary<string, ProviderUsageStatus>> History => _historyCache;

    public UsageHistoryService(IAppDataPaths appDataPaths, CodexBarWindows.Abstractions.IClock clock)
    {
        _appDataPaths = appDataPaths;
        _clock = clock;
        _historyCache = LoadHistory();
    }

    public void RecordSnapshot(List<ProviderUsageStatus> statuses)
    {
        var today = _clock.UtcNow.ToString("yyyy-MM-dd");
        if (!_historyCache.ContainsKey(today))
        {
            _historyCache[today] = new Dictionary<string, ProviderUsageStatus>();
        }

        bool updated = false;
        foreach (var status in statuses)
        {
            if (status.IsError) continue; // Don't record errors as usage data
            
            _historyCache[today][status.ProviderId] = status;
            updated = true;
        }

        if (updated)
        {
            SaveHistory();
        }
    }

    private Dictionary<string, Dictionary<string, ProviderUsageStatus>> LoadHistory()
    {
        if (File.Exists(_appDataPaths.HistoryFilePath))
        {
            try
            {
                var json = File.ReadAllText(_appDataPaths.HistoryFilePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, ProviderUsageStatus>>>(json);
                if (dict != null) return dict;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load usage history: {ex.Message}");
            }
        }
        return new Dictionary<string, Dictionary<string, ProviderUsageStatus>>();
    }

    private void SaveHistory()
    {
        try
        {
            var dir = Path.GetDirectoryName(_appDataPaths.HistoryFilePath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var opts = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_historyCache, opts);
            File.WriteAllText(_appDataPaths.HistoryFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save usage history: {ex.Message}");
        }
    }
}
