using System.Text.RegularExpressions;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

/// <summary>
/// Kiro provider — uses the kiro-cli to fetch usage data.
/// Runs `kiro-cli chat --no-interactive /usage` and parses the output
/// for credits percentage, plan name, and bonus credits.
/// </summary>
public class KiroProvider : IProviderProbe
{
    private readonly CliExecutionHelper _cliHelper;
    private readonly SettingsService _settings;

    public string ProviderId => "kiro";
    public string ProviderName => "Kiro";
    public bool IsEnabled => _settings.CurrentSettings.EnabledProviders.GetValueOrDefault("kiro", false);

    public KiroProvider(CliExecutionHelper cliHelper, SettingsService settings)
    {
        _cliHelper = cliHelper;
        _settings = settings;
    }

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken ct)
    {
        // 1. Check if kiro-cli is available
        var versionResult = await _cliHelper.ExecuteCommandAsync("kiro-cli", "--version", 5000);
        if (versionResult.ExitCode != 0)
            return MakeError("kiro-cli not found. Install it from https://kiro.dev");

        // 2. Check login
        var whoamiResult = await _cliHelper.ExecuteCommandAsync("kiro-cli", "whoami", 5000);
        var whoamiLower = (whoamiResult.Output + whoamiResult.Error).ToLowerInvariant();
        if (whoamiLower.Contains("not logged in") || whoamiLower.Contains("login required"))
            return MakeError("Not logged in to Kiro. Run 'kiro-cli login' first.");

        // 3. Fetch usage
        var usageResult = await _cliHelper.ExecuteCommandAsync(
            "kiro-cli", "chat --no-interactive /usage", 20000);

        var output = usageResult.Output + usageResult.Error;
        if (string.IsNullOrWhiteSpace(output))
            return MakeError("No output from kiro-cli.");

        return ParseUsageOutput(output);
    }

    private static ProviderUsageStatus ParseUsageOutput(string rawOutput)
    {
        var stripped = StripAnsi(rawOutput);
        var lower = stripped.ToLowerInvariant();

        if (lower.Contains("not logged in") || lower.Contains("login required"))
            return MakeError("Not logged in to Kiro. Run 'kiro-cli login' first.");

        // Parse plan name
        string planName = "Kiro";
        var planMatch = Regex.Match(stripped, @"Plan:\s*(.+)", RegexOptions.IgnoreCase);
        if (planMatch.Success)
            planName = planMatch.Groups[1].Value.Trim().Split('\n')[0].Trim();
        else
        {
            var legacyPlan = Regex.Match(stripped, @"\|\s*(KIRO\s+\w+)", RegexOptions.IgnoreCase);
            if (legacyPlan.Success)
                planName = legacyPlan.Groups[1].Value.Trim();
        }

        // Parse credits percentage from "████...█ X%"
        double creditsPercent = 0;
        var percentMatch = Regex.Match(stripped, @"█+\s*(\d+)%");
        if (percentMatch.Success)
            creditsPercent = double.Parse(percentMatch.Groups[1].Value);

        // Parse credits used/total from "(X of Y covered in plan)"
        double creditsUsed = 0, creditsTotal = 50;
        var creditsMatch = Regex.Match(stripped, @"\((\d+\.?\d*)\s+of\s+(\d+)\s+covered");
        if (creditsMatch.Success)
        {
            creditsUsed = double.Parse(creditsMatch.Groups[1].Value);
            creditsTotal = double.Parse(creditsMatch.Groups[2].Value);
            if (creditsPercent == 0 && creditsTotal > 0)
                creditsPercent = (creditsUsed / creditsTotal) * 100.0;
        }

        // Parse bonus credits
        string? bonusInfo = null;
        var bonusMatch = Regex.Match(stripped, @"Bonus credits:\s*(\d+\.?\d*)/(\d+)");
        if (bonusMatch.Success)
        {
            bonusInfo = $"Bonus: {bonusMatch.Groups[1].Value}/{bonusMatch.Groups[2].Value}";
            var expiryMatch = Regex.Match(stripped, @"expires in (\d+) days?");
            if (expiryMatch.Success)
                bonusInfo += $" (expires in {expiryMatch.Groups[1].Value}d)";
        }

        var tooltipParts = new List<string> { "Kiro", $"Plan: {planName}" };
        tooltipParts.Add($"Credits: {creditsUsed:F1}/{creditsTotal} ({creditsPercent:F0}%)");
        if (bonusInfo != null) tooltipParts.Add(bonusInfo);

        return new ProviderUsageStatus
        {
            ProviderId = "kiro",
            ProviderName = "Kiro",
            SessionProgress = Math.Min(1.0, creditsPercent / 100.0),
            WeeklyProgress = 0.0,
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    private static string StripAnsi(string text)
    {
        return Regex.Replace(text, @"\x1B\[[0-9;?]*[A-Za-z]|\x1B\].*?\x07", "");
    }

    private static ProviderUsageStatus MakeError(string message) => new()
    {
        ProviderId = "kiro",
        ProviderName = "Kiro",
        IsError = true,
        ErrorMessage = message,
        TooltipText = $"Kiro: {message}"
    };
}
