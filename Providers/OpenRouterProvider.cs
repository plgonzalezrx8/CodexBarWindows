using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

/// <summary>
/// OpenRouter provider — queries https://openrouter.ai/api/v1/auth/key
/// using an API key to get credits and usage information.
/// </summary>
public class OpenRouterProvider : IProviderProbe
{
    private readonly SettingsService _settings;

    private const string AuthKeyEndpoint = "https://openrouter.ai/api/v1/auth/key";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public string ProviderId => "openrouter";
    public string ProviderName => "OpenRouter";
    public bool IsEnabled => _settings.CurrentSettings.EnabledProviders.GetValueOrDefault("openrouter", false);

    public OpenRouterProvider(SettingsService settings)
    {
        _settings = settings;
    }

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken ct)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrEmpty(apiKey))
            return MakeError("No OpenRouter API key. Set OPENROUTER_API_KEY env var or add it in Settings.");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, AuthKeyEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var resp = await Http.SendAsync(req, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return MakeError("OpenRouter API key is invalid.");

            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            return ParseResponse(json);
        }
        catch (Exception ex)
        {
            return MakeError($"OpenRouter API error: {ex.Message}");
        }
    }

    private static ProviderUsageStatus ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // OpenRouter /auth/key returns { data: { label, usage, limit, ... } }
        if (!root.TryGetProperty("data", out var data))
            return MakeError("Unexpected OpenRouter response format.");

        var label = data.TryGetProperty("label", out var l) ? l.GetString() ?? "Key" : "Key";
        var usage = data.TryGetProperty("usage", out var u) ? u.GetDouble() : 0.0;
        var limit = data.TryGetProperty("limit", out var lim) ? lim.GetDouble() : 0.0;
        var limitRemaining = data.TryGetProperty("limit_remaining", out var lr) ? lr.GetDouble() : 0.0;

        var tooltipParts = new List<string> { "OpenRouter" };
        tooltipParts.Add($"Key: {label}");
        tooltipParts.Add($"Usage: ${usage:F2}");
        if (limit > 0)
        {
            tooltipParts.Add($"Limit: ${limit:F2}");
            tooltipParts.Add($"Remaining: ${limitRemaining:F2}");
        }

        double sessionProgress = limit > 0 ? Math.Min(1.0, usage / limit) : 0.0;

        return new ProviderUsageStatus
        {
            ProviderId = "openrouter",
            ProviderName = "OpenRouter",
            SessionProgress = sessionProgress,
            WeeklyProgress = 0.0,
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    private static string? ResolveApiKey()
    {
        return Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
    }

    private static ProviderUsageStatus MakeError(string message) => new()
    {
        ProviderId = "openrouter",
        ProviderName = "OpenRouter",
        IsError = true,
        ErrorMessage = message,
        TooltipText = $"OpenRouter: {message}"
    };
}
