using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexBarWindows.Abstractions;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

/// <summary>
/// Copilot provider — uses a GitHub personal access token (PAT) or
/// CODEXBAR_COPILOT_TOKEN env var to query the GitHub Copilot usage API.
/// </summary>
public class CopilotProvider : IProviderProbe
{
    private readonly SettingsService _settings;
    private readonly IEnvironmentService _environmentService;
    private readonly HttpClient _httpClient;

    private const string UsageEndpoint = "https://api.github.com/copilot/usage";
    private const string UserEndpoint  = "https://api.github.com/user";

    public string ProviderId => "copilot";
    public string ProviderName => "Copilot";
    public bool IsEnabled => _settings.CurrentSettings.EnabledProviders.GetValueOrDefault("copilot", false);

    public CopilotProvider(SettingsService settings, IEnvironmentService environmentService, HttpClient httpClient)
    {
        _settings = settings;
        _environmentService = environmentService;
        _httpClient = httpClient;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CodexBarWindows/1.0");
        }

        if (!_httpClient.DefaultRequestHeaders.Accept.Any())
        {
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        if (!_httpClient.DefaultRequestHeaders.Contains("X-GitHub-Api-Version"))
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        }
    }

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken ct)
    {
        var token = ResolveToken();
        if (string.IsNullOrEmpty(token))
            return MakeError("No Copilot token. Set CODEXBAR_COPILOT_TOKEN env var or add a GitHub PAT in Settings.");

        try
        {
            // Get user info
            string? login = null;
            try
            {
                using var userReq = new HttpRequestMessage(HttpMethod.Get, UserEndpoint);
                userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var userResp = await _httpClient.SendAsync(userReq, ct);
                if (userResp.IsSuccessStatusCode)
                {
                    var userJson = await userResp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(userJson);
                    login = doc.RootElement.TryGetProperty("login", out var l) ? l.GetString() : null;
                }
            }
            catch { /* Best effort */ }

            // Get usage — individual user endpoint
            using var req = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _httpClient.SendAsync(req, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return MakeError("Copilot token is invalid or expired.");
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return MakeError("Copilot usage API not available. Check your plan.");

            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            return ParseUsageResponse(json, login);
        }
        catch (Exception ex)
        {
            return MakeError($"Copilot API error: {ex.Message}");
        }
    }

    internal static ProviderUsageStatus ParseUsageResponse(string json, string? login)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // The response may be an array of daily usage or a summary object
        int totalCompletions = 0, totalSuggestions = 0;

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var day in root.EnumerateArray())
            {
                totalCompletions += day.TryGetProperty("total_acceptances_count", out var ac) ? ac.GetInt32() : 0;
                totalSuggestions += day.TryGetProperty("total_suggestions_count", out var sc) ? sc.GetInt32() : 0;
            }
        }

        var tooltipParts = new List<string> { "Copilot" };
        if (login != null) tooltipParts.Add($"Account: {login}");
        tooltipParts.Add($"Suggestions: {totalSuggestions}");
        tooltipParts.Add($"Acceptances: {totalCompletions}");

        // Copilot doesn't have strict quotas, so we show acceptance rate
        double acceptRate = totalSuggestions > 0 ? (double)totalCompletions / totalSuggestions : 0;

        return new ProviderUsageStatus
        {
            ProviderId = "copilot",
            ProviderName = "Copilot",
            SessionProgress = acceptRate,
            WeeklyProgress = 0.0,
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    private string? ResolveToken()
    {
        return _environmentService.GetEnvironmentVariable("CODEXBAR_COPILOT_TOKEN")
            ?? _environmentService.GetEnvironmentVariable("GITHUB_COPILOT_TOKEN");
    }

    private static ProviderUsageStatus MakeError(string message) => new()
    {
        ProviderId = "copilot",
        ProviderName = "Copilot",
        IsError = true,
        ErrorMessage = message,
        TooltipText = $"Copilot: {message}"
    };
}
