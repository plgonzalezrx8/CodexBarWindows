using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexBarWindows.Abstractions;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

/// <summary>
/// Cursor provider — reads session cookies from Chrome/Edge to authenticate
/// against cursor.com's usage-summary API.
///
/// API endpoints:
///   - GET /api/usage-summary  → plan usage, on-demand usage, billing cycle
///   - GET /api/auth/me        → user email, name, sub (user id)
///   - GET /api/usage?user=ID  → legacy request-based plan usage
/// </summary>
public class CursorProvider : IProviderProbe
{
    private readonly IBrowserCookieSource _cookieSource;
    private readonly ICredentialStore _credentialStore;
    private readonly SettingsService _settings;
    private readonly HttpClient _httpClient;

    private const string BaseUrl = "https://cursor.com";
    private static readonly string[] CookieDomains = ["cursor.com", "cursor.sh"];
    private static readonly string[] SessionCookieNames =
        ["WorkosCursorSessionToken", "__Secure-next-auth.session-token", "next-auth.session-token"];

    public string ProviderId => "cursor";
    public string ProviderName => "Cursor";
    public bool IsEnabled => _settings.CurrentSettings.EnabledProviders.GetValueOrDefault("cursor", true);

    public CursorProvider(
        IBrowserCookieSource cookieSource,
        ICredentialStore credentialStore,
        SettingsService settings,
        HttpClient httpClient)
    {
        _cookieSource = cookieSource;
        _credentialStore = credentialStore;
        _settings = settings;
        _httpClient = httpClient;
    }

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken cancellationToken)
    {
        // 1. Try cached cookie header from Credential Manager
        var cached = _credentialStore.GetCachedCookieHeader("cursor");
        if (cached != null && !string.IsNullOrWhiteSpace(cached.CookieHeader))
        {
            try
            {
                return await FetchWithCookieHeader(cached.CookieHeader, cancellationToken);
            }
            catch (CursorAuthException)
            {
                _credentialStore.ClearCachedCookieHeader("cursor");
            }
        }

        // 2. Try importing cookies from browsers
        foreach (var domain in CookieDomains)
        {
            var cookieHeader = _cookieSource.GetCookieHeader(domain);
            if (string.IsNullOrEmpty(cookieHeader)) continue;

            // Verify we have a session cookie
            if (!SessionCookieNames.Any(name => cookieHeader.Contains(name)))
                continue;

            try
            {
                var status = await FetchWithCookieHeader(cookieHeader, cancellationToken);
                // Cache successful cookies
                _credentialStore.CacheCookieHeader("cursor", cookieHeader, "browser-auto");
                return status;
            }
            catch (CursorAuthException)
            {
                // Try next domain
            }
        }

        // 3. Try manual cookie from Credential Manager
        var manualCookie = _credentialStore.GetCredential("cursor", "manual-cookie");
        if (!string.IsNullOrEmpty(manualCookie))
        {
            try
            {
                return await FetchWithCookieHeader(manualCookie, cancellationToken);
            }
            catch (CursorAuthException)
            {
                // Manual cookie is stale
            }
        }

        return MakeError("No Cursor session found. Log in to cursor.com in Chrome or Edge.");
    }

    // ── HTTP Fetch ──────────────────────────────────────────────────

    private async Task<ProviderUsageStatus> FetchWithCookieHeader(
        string cookieHeader, CancellationToken ct)
    {
        // Fetch usage summary
        var (summary, rawJson) = await FetchUsageSummary(cookieHeader, ct);

        // Fetch user info (optional)
        CursorUserInfo? userInfo = null;
        try { userInfo = await FetchUserInfo(cookieHeader, ct); } catch { /* optional */ }

        // Parse results
        var planUsedRaw = (double)(summary.IndividualUsage?.Plan?.Used ?? 0);
        var planLimitRaw = (double)(summary.IndividualUsage?.Plan?.Limit ?? 0);
        var planUsedUsd = planUsedRaw / 100.0;
        var planLimitUsd = planLimitRaw / 100.0;

        double planPercentUsed;
        if (planLimitRaw > 0)
        {
            planPercentUsed = (planUsedRaw / planLimitRaw) * 100;
        }
        else
        {
            planPercentUsed = summary.IndividualUsage?.Plan?.TotalPercentUsed ?? 0;

            // Normalize percent if the fallback API returns a 0-1 fraction.
            if (planPercentUsed is > 0 and <= 1)
            {
                planPercentUsed *= 100;
            }
        }

        var onDemandUsedUsd = (summary.IndividualUsage?.OnDemand?.Used ?? 0) / 100.0;
        var onDemandLimitUsd = summary.IndividualUsage?.OnDemand?.Limit is int odLimit ? odLimit / 100.0 : (double?)null;

        // Build tooltip
        var tooltipParts = new List<string> { "Cursor" };
        tooltipParts.Add($"Plan: {planPercentUsed:F1}% used (${planUsedUsd:F2} / ${planLimitUsd:F2})");
        if (onDemandUsedUsd > 0)
            tooltipParts.Add($"On-demand: ${onDemandUsedUsd:F2}" + (onDemandLimitUsd.HasValue ? $" / ${onDemandLimitUsd:F2}" : ""));
        if (!string.IsNullOrEmpty(summary.MembershipType))
            tooltipParts.Add($"Plan: Cursor {char.ToUpper(summary.MembershipType[0])}{summary.MembershipType[1..]}");
        if (userInfo?.Email != null)
            tooltipParts.Add($"Account: {userInfo.Email}");
        if (summary.BillingCycleEnd != null)
            tooltipParts.Add($"Resets: {summary.BillingCycleEnd}");

        return new ProviderUsageStatus
        {
            ProviderId = "cursor",
            ProviderName = "Cursor",
            SessionProgress = Math.Min(1.0, planPercentUsed / 100.0),
            WeeklyProgress = onDemandLimitUsd.HasValue && onDemandLimitUsd > 0
                ? Math.Min(1.0, onDemandUsedUsd / onDemandLimitUsd.Value)
                : 0.0,
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    private async Task<(CursorUsageSummary Summary, string RawJson)> FetchUsageSummary(
        string cookieHeader, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/usage-summary");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Cookie", cookieHeader);

        using var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new CursorAuthException("Not logged in");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var summary = JsonSerializer.Deserialize<CursorUsageSummary>(json, JsonOpts)
                   ?? throw new Exception("Failed to parse usage-summary");
        return (summary, json);
    }

    private async Task<CursorUserInfo> FetchUserInfo(string cookieHeader, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/auth/me");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Cookie", cookieHeader);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<CursorUserInfo>(json, JsonOpts)
            ?? throw new Exception("Failed to parse auth/me");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static ProviderUsageStatus MakeError(string message) => new()
    {
        ProviderId = "cursor",
        ProviderName = "Cursor",
        IsError = true,
        ErrorMessage = message,
        TooltipText = $"Cursor: {message}"
    };

    private class CursorAuthException(string message) : Exception(message);
}

// ── Cursor API Models ───────────────────────────────────────────────

public class CursorUsageSummary
{
    [JsonPropertyName("billingCycleStart")]
    public string? BillingCycleStart { get; set; }

    [JsonPropertyName("billingCycleEnd")]
    public string? BillingCycleEnd { get; set; }

    [JsonPropertyName("membershipType")]
    public string? MembershipType { get; set; }

    [JsonPropertyName("individualUsage")]
    public CursorIndividualUsage? IndividualUsage { get; set; }
}

public class CursorIndividualUsage
{
    [JsonPropertyName("plan")]
    public CursorPlanUsage? Plan { get; set; }

    [JsonPropertyName("onDemand")]
    public CursorOnDemandUsage? OnDemand { get; set; }
}

public class CursorPlanUsage
{
    [JsonPropertyName("used")]
    public int? Used { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("totalPercentUsed")]
    public double? TotalPercentUsed { get; set; }
}

public class CursorOnDemandUsage
{
    [JsonPropertyName("used")]
    public int? Used { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}

public class CursorUserInfo
{
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("sub")]
    public string? Sub { get; set; }
}
