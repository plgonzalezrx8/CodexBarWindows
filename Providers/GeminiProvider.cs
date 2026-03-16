using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

/// <summary>
/// Gemini provider — reads OAuth credentials from ~/.gemini/oauth_creds.json
/// (written by the Gemini CLI) and calls the Google Cloud Code Private API
/// to retrieve per-model quota buckets.
///
/// Supports automatic token refresh using the Gemini CLI's client ID/secret.
/// </summary>
public class GeminiProvider : IProviderProbe
{
    private readonly SettingsService _settings;

    private const string QuotaEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
    private const string TokenRefreshEndpoint = "https://oauth2.googleapis.com/token";
    private const string CredentialsRelPath = @".gemini\oauth_creds.json";
    private const string SettingsRelPath = @".gemini\settings.json";

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public string ProviderId => "gemini";
    public string ProviderName => "Gemini";
    public bool IsEnabled => _settings.CurrentSettings.EnabledProviders.GetValueOrDefault("gemini", false);

    public GeminiProvider(SettingsService settings)
    {
        _settings = settings;
    }

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken cancellationToken)
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Check auth type from settings
        var authType = ReadAuthType(homeDir);
        if (authType is "api-key" or "vertex-ai")
            return MakeError($"Gemini {authType} auth not supported. Use Google account (OAuth) instead.");

        // Load OAuth credentials
        var creds = LoadCredentials(homeDir);
        if (creds == null)
            return MakeError("Not logged in to Gemini. Run `gemini` in Terminal to authenticate.");

        if (string.IsNullOrEmpty(creds.AccessToken))
            return MakeError("No Gemini access token found. Run `gemini` to log in.");

        var accessToken = creds.AccessToken;

        // Refresh if expired
        if (creds.ExpiryDate.HasValue && creds.ExpiryDate.Value < DateTime.UtcNow)
        {
            if (string.IsNullOrEmpty(creds.RefreshToken))
                return MakeError("Gemini token expired and no refresh token available. Run `gemini` to re-authenticate.");

            try
            {
                accessToken = await RefreshAccessToken(creds.RefreshToken, homeDir, cancellationToken);
            }
            catch (Exception ex)
            {
                return MakeError($"Token refresh failed: {ex.Message}");
            }
        }

        // Fetch quota
        try
        {
            return await FetchQuota(accessToken, creds.IdToken, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return MakeError("Gemini session expired. Run `gemini` to re-authenticate.");
        }
        catch (Exception ex)
        {
            return MakeError($"Gemini API error: {ex.Message}");
        }
    }

    // ── Quota Fetch ─────────────────────────────────────────────────

    private static async Task<ProviderUsageStatus> FetchQuota(
        string accessToken, string? idToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, QuotaEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var quotaResponse = JsonSerializer.Deserialize<GeminiQuotaResponse>(json, JsonOpts);

        if (quotaResponse?.Buckets == null || quotaResponse.Buckets.Count == 0)
            return MakeError("No quota buckets in Gemini response.");

        // Group by model, keep lowest remaining fraction per model
        var modelQuotas = new Dictionary<string, (double fraction, string? resetTime)>();
        foreach (var bucket in quotaResponse.Buckets)
        {
            if (string.IsNullOrEmpty(bucket.ModelId) || !bucket.RemainingFraction.HasValue)
                continue;

            if (!modelQuotas.TryGetValue(bucket.ModelId, out var existing) ||
                bucket.RemainingFraction.Value < existing.fraction)
            {
                modelQuotas[bucket.ModelId] = (bucket.RemainingFraction.Value, bucket.ResetTime);
            }
        }

        // Find the lowest per-model quota for icon display
        double? lowestRemaining = null;
        string? lowestModelId = null;

        // Categorize into pro/flash/flash-lite
        double proUsed = 0, flashUsed = 0;
        foreach (var (modelId, (fraction, _)) in modelQuotas)
        {
            var lower = modelId.ToLowerInvariant();
            var usedPct = (1 - fraction) * 100;

            if (lower.Contains("pro")) proUsed = Math.Max(proUsed, usedPct);
            else if (lower.Contains("flash")) flashUsed = Math.Max(flashUsed, usedPct);

            if (!lowestRemaining.HasValue || fraction < lowestRemaining.Value)
            {
                lowestRemaining = fraction;
                lowestModelId = modelId;
            }
        }

        // Extract email from JWT id_token
        var email = ExtractEmailFromJwt(idToken);

        var tooltipParts = new List<string> { "Gemini" };
        if (proUsed > 0) tooltipParts.Add($"Pro: {proUsed:F1}% used");
        if (flashUsed > 0) tooltipParts.Add($"Flash: {flashUsed:F1}% used");
        tooltipParts.Add($"Models tracked: {modelQuotas.Count}");
        if (email != null) tooltipParts.Add($"Account: {email}");

        return new ProviderUsageStatus
        {
            SessionProgress = proUsed / 100.0,
            WeeklyProgress = flashUsed / 100.0,
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    // ── Token Refresh ───────────────────────────────────────────────

    private static async Task<string> RefreshAccessToken(
        string refreshToken, string homeDir, CancellationToken ct)
    {
        // Find OAuth client credentials from the Gemini CLI installation
        var (clientId, clientSecret) = FindGeminiOAuthCredentials();

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        });

        using var response = await HttpClient.PostAsync(TokenRefreshEndpoint, body, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var newAccessToken = root.GetProperty("access_token").GetString()
            ?? throw new Exception("No access_token in refresh response");

        // Update stored credentials
        UpdateStoredCredentials(homeDir, root);

        return newAccessToken;
    }

    private static void UpdateStoredCredentials(string homeDir, JsonElement refreshResponse)
    {
        var credsPath = Path.Combine(homeDir, CredentialsRelPath);
        if (!File.Exists(credsPath)) return;

        try
        {
            var existingJson = File.ReadAllText(credsPath);
            using var existingDoc = JsonDocument.Parse(existingJson);
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson) ?? [];

            if (refreshResponse.TryGetProperty("access_token", out var at))
                dict["access_token"] = at;
            if (refreshResponse.TryGetProperty("expires_in", out var ei))
            {
                var expiresIn = ei.GetDouble();
                var expiryMs = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)(expiresIn * 1000));
                dict["expiry_date"] = JsonDocument.Parse(expiryMs.ToString()).RootElement;
            }
            if (refreshResponse.TryGetProperty("id_token", out var idt))
                dict["id_token"] = idt;

            var updatedJson = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(credsPath, updatedJson);
        }
        catch { /* Best effort */ }
    }

    // ── Credential Loading ──────────────────────────────────────────

    private class OAuthCredentials
    {
        public string? AccessToken { get; set; }
        public string? IdToken { get; set; }
        public string? RefreshToken { get; set; }
        public DateTime? ExpiryDate { get; set; }
    }

    private static OAuthCredentials? LoadCredentials(string homeDir)
    {
        var credsPath = Path.Combine(homeDir, CredentialsRelPath);
        if (!File.Exists(credsPath)) return null;

        try
        {
            var json = File.ReadAllText(credsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            DateTime? expiryDate = null;
            if (root.TryGetProperty("expiry_date", out var expiryProp))
            {
                var expiryMs = expiryProp.GetDouble();
                expiryDate = DateTimeOffset.FromUnixTimeMilliseconds((long)expiryMs).UtcDateTime;
            }

            return new OAuthCredentials
            {
                AccessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null,
                IdToken = root.TryGetProperty("id_token", out var it) ? it.GetString() : null,
                RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                ExpiryDate = expiryDate
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadAuthType(string homeDir)
    {
        var settingsPath = Path.Combine(homeDir, SettingsRelPath);
        if (!File.Exists(settingsPath)) return null;

        try
        {
            var json = File.ReadAllText(settingsPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("security", out var sec) &&
                sec.TryGetProperty("auth", out var auth) &&
                auth.TryGetProperty("selectedType", out var sel))
            {
                return sel.GetString();
            }
        }
        catch { }
        return null;
    }

    // ── JWT Email Extraction ────────────────────────────────────────

    private static string? ExtractEmailFromJwt(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken)) return null;
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var bytes = Convert.FromBase64String(payload);
            var json = Encoding.UTF8.GetString(bytes);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("email", out var email) ? email.GetString() : null;
        }
        catch { return null; }
    }

    // ── Gemini CLI OAuth Discovery ──────────────────────────────────

    private static (string ClientId, string ClientSecret) FindGeminiOAuthCredentials()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 1. Try reading client_id/client_secret from oauth_creds.json itself
        //    (Gemini CLI stores them alongside the tokens)
        var credsPath = Path.Combine(homeDir, CredentialsRelPath);
        if (File.Exists(credsPath))
        {
            try
            {
                var json = File.ReadAllText(credsPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("client_id", out var ci) &&
                    root.TryGetProperty("client_secret", out var cs))
                {
                    var clientId = ci.GetString();
                    var clientSecret = cs.GetString();
                    if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
                        return (clientId, clientSecret);
                }
            }
            catch { /* Fall through to JS file parsing */ }
        }

        // 2. Try extracting from the Gemini CLI's installed oauth2.js
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var searchPaths = new[]
        {
            Path.Combine(appData, @"npm\node_modules\@google\gemini-cli\node_modules\@google\gemini-cli-core\dist\src\code_assist\oauth2.js"),
            Path.Combine(localAppData, @"bun\node_modules\@google\gemini-cli\node_modules\@google\gemini-cli-core\dist\src\code_assist\oauth2.js"),
        };

        foreach (var path in searchPaths)
        {
            if (!File.Exists(path)) continue;
            var content = File.ReadAllText(path);
            var result = ParseOAuthFromJs(content);
            if (result.HasValue) return result.Value;
        }

        throw new Exception("Could not find Gemini OAuth client credentials. Ensure the Gemini CLI is installed and you have logged in with `gemini`.");
    }

    private static (string ClientId, string ClientSecret)? ParseOAuthFromJs(string content)
    {
        var idMatch = System.Text.RegularExpressions.Regex.Match(content, @"OAUTH_CLIENT_ID\s*=\s*['""]([^'""]+)['""]");
        var secretMatch = System.Text.RegularExpressions.Regex.Match(content, @"OAUTH_CLIENT_SECRET\s*=\s*['""]([^'""]+)['""]");

        if (idMatch.Success && secretMatch.Success)
            return (idMatch.Groups[1].Value, secretMatch.Groups[1].Value);
        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static ProviderUsageStatus MakeError(string message) => new()
    {
        IsError = true,
        ErrorMessage = message,
        TooltipText = $"Gemini: {message}"
    };
}

// ── Gemini Quota API Models ─────────────────────────────────────────

public class GeminiQuotaResponse
{
    [JsonPropertyName("buckets")]
    public List<GeminiQuotaBucket>? Buckets { get; set; }
}

public class GeminiQuotaBucket
{
    [JsonPropertyName("remainingFraction")]
    public double? RemainingFraction { get; set; }

    [JsonPropertyName("resetTime")]
    public string? ResetTime { get; set; }

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("tokenType")]
    public string? TokenType { get; set; }
}
