using CodexBarWindows.Models;

namespace CodexBarWindows.Services;

public class MockProvider : IProviderProbe
{
    private Random _random = new();

    public string ProviderId => "mock_provider";
    public string ProviderName => "Mock AI";
    public bool IsEnabled => true;

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken cancellationToken)
    {
        // Simulate network delay
        await Task.Delay(500, cancellationToken);

        // Generate random usage stats
        double session = _random.NextDouble();
        double weekly = _random.NextDouble();

        return new ProviderUsageStatus
        {
            SessionProgress = session,
            WeeklyProgress = weekly,
            IsError = false,
            TooltipText = $"Mock AI\nSession: {(session * 100):F1}%\nWeekly: {(weekly * 100):F1}%"
        };
    }
}
