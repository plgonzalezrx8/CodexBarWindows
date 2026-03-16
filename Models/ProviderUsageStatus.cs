namespace CodexBarWindows.Models;

public class ProviderUsageStatus
{
    public double SessionProgress { get; set; } = 0.0;
    public double WeeklyProgress { get; set; } = 0.0;
    public bool IsError { get; set; } = false;
    public string? ErrorMessage { get; set; }
    public string TooltipText { get; set; } = string.Empty;
}

public interface IProviderProbe
{
    string ProviderId { get; }
    string ProviderName { get; }
    bool IsEnabled { get; }
    Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken cancellationToken);
}
