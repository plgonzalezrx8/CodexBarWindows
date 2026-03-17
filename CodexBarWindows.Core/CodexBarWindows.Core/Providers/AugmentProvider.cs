using System.Net.Http;
using System.Text.Json;
using CodexBarWindows.Abstractions;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

/// <summary>
/// Augment provider — uses browser cookies to authenticate with the
/// Augment app API (app.augmentcode.com/api/credits) for credits usage.
/// Falls back to cached credentials when browser cookies unavailable.
/// </summary>
public class AugmentProvider : IProviderProbe
{
    private readonly SettingsService _settings;
    private readonly IBrowserCookieSource _cookieSource;
    private readonly ICredentialStore _credentialStore;
    private readonly HttpClient _httpClient;

    private const string CreditsEndpoint = "https://app.augmentcode.com/api/credits";
    private const string SubscriptionEndpoint = "https://app.augmentcode.com/api/subscription";

    public string ProviderId => "augment";
    public string ProviderName => "Augment";
    public bool IsEnabled => _settings.CurrentSettings.EnabledProviders.GetValueOrDefault("augment", false);

    public AugmentProvider(
        SettingsService settings,
        IBrowserCookieSource cookieSource,
        ICredentialStore credentialStore,
        HttpClient httpClient)
    {
        _settings = settings;
        _cookieSource = cookieSource;
        _credentialStore = credentialStore;
        _httpClient = httpClient;
    }

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken ct)
    {
        // Try cached cookie header first
        var cachedEntry = _credentialStore.GetCachedCookieHeader("augment");
        if (cachedEntry != null && !string.IsNullOrEmpty(cachedEntry.CookieHeader))
        {
            try
            {
                return await FetchWithCookies(cachedEntry.CookieHeader, ct);
            }
            catch
            {
                _credentialStore.ClearCachedCookieHeader("augment");
            }
        }

        // Try auto-importing from browsers
        try
        {
            var cookieHeader = _cookieSource.GetCookieHeader("augmentcode.com");
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                var result = await FetchWithCookies(cookieHeader, ct);

                // Cache on success
                _credentialStore.CacheCookieHeader("augment", cookieHeader, "browser-auto");
                return result;
            }
        }
        catch { /* Fall through */ }

        return MakeError("No Augment session. Log in to app.augmentcode.com in your browser.");
    }

    private async Task<ProviderUsageStatus> FetchWithCookies(string cookieHeader, CancellationToken ct)
    {
        // Fetch credits
        using var credReq = new HttpRequestMessage(HttpMethod.Get, CreditsEndpoint);
        credReq.Headers.Add("Cookie", cookieHeader);
        credReq.Headers.Add("Accept", "application/json");

        using var credResp = await _httpClient.SendAsync(credReq, ct);

        if (credResp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
            credResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Augment session expired.");

        credResp.EnsureSuccessStatusCode();

        var credJson = await credResp.Content.ReadAsStringAsync(ct);
        using var credDoc = JsonDocument.Parse(credJson);
        var credRoot = credDoc.RootElement;

        var remaining = credRoot.TryGetProperty("usageUnitsRemaining", out var r) ? r.GetDouble() : 0;
        var consumed = credRoot.TryGetProperty("usageUnitsConsumedThisBillingCycle", out var c) ? c.GetDouble() : 0;
        var total = remaining + consumed;

        // Fetch subscription (best effort)
        string? planName = null, email = null;
        try
        {
            using var subReq = new HttpRequestMessage(HttpMethod.Get, SubscriptionEndpoint);
            subReq.Headers.Add("Cookie", cookieHeader);
            subReq.Headers.Add("Accept", "application/json");

            using var subResp = await _httpClient.SendAsync(subReq, ct);
            if (subResp.IsSuccessStatusCode)
            {
                var subJson = await subResp.Content.ReadAsStringAsync(ct);
                using var subDoc = JsonDocument.Parse(subJson);
                var subRoot = subDoc.RootElement;
                planName = subRoot.TryGetProperty("planName", out var p) ? p.GetString() : null;
                email = subRoot.TryGetProperty("email", out var e) ? e.GetString() : null;
            }
        }
        catch { /* Best effort */ }

        double usedPercent = total > 0 ? (consumed / total) * 100.0 : 0;

        var tooltipParts = new List<string> { "Augment" };
        if (planName != null) tooltipParts.Add($"Plan: {planName}");
        if (email != null) tooltipParts.Add($"Account: {email}");
        tooltipParts.Add($"Credits: {consumed:F0}/{total:F0} ({usedPercent:F0}% used)");
        tooltipParts.Add($"Remaining: {remaining:F0}");

        return new ProviderUsageStatus
        {
            ProviderId = "augment",
            ProviderName = "Augment",
            SessionProgress = Math.Min(1.0, usedPercent / 100.0),
            WeeklyProgress = 0.0,
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    private static ProviderUsageStatus MakeError(string message) => new()
    {
        ProviderId = "augment",
        ProviderName = "Augment",
        IsError = true,
        ErrorMessage = message,
        TooltipText = $"Augment: {message}"
    };
}
