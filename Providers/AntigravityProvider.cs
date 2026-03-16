using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

/// <summary>
/// Antigravity provider — detects the local Antigravity language server process,
/// reads its CSRF token and listening port from command-line arguments,
/// and calls its gRPC-like HTTP API for quota data.
///
/// Windows-specific: uses `wmic`/`Get-CimInstance` instead of macOS `ps`/`lsof`
/// for process detection and port discovery.
/// </summary>
public class AntigravityProvider : IProviderProbe
{
    private readonly CliExecutionHelper _cliHelper;
    private readonly SettingsService _settings;

    private const string GetUserStatusPath   = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
    private const string GetModelConfigPath   = "/exa.language_server_pb.LanguageServerService/GetCommandModelConfigs";
    private static readonly string[] ProcessNames = ["language_server_windows", "language_server"];

    public string ProviderId => "antigravity";
    public string ProviderName => "Antigravity";
    public bool IsEnabled => _settings.CurrentSettings.EnabledProviders.GetValueOrDefault("antigravity", false);

    public AntigravityProvider(CliExecutionHelper cliHelper, SettingsService settings)
    {
        _cliHelper = cliHelper;
        _settings = settings;
    }

    public async Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken cancellationToken)
    {
        // 1. Detect Antigravity language server process
        ProcessInfo? processInfo;
        try
        {
            processInfo = await DetectProcessInfo(cancellationToken);
        }
        catch (Exception ex)
        {
            return MakeError(ex.Message);
        }

        if (processInfo == null)
            return MakeError("Antigravity language server not detected. Launch Antigravity and retry.");

        // 2. Query the API for user status
        try
        {
            var response = await MakeApiRequest(
                processInfo.Port, processInfo.CsrfToken,
                GetUserStatusPath, DefaultRequestBody(), cancellationToken);

            return ParseUserStatusResponse(response);
        }
        catch (Exception ex1)
        {
            // Fallback: try command model configs endpoint
            try
            {
                var response = await MakeApiRequest(
                    processInfo.Port, processInfo.CsrfToken,
                    GetModelConfigPath, DefaultRequestBody(), cancellationToken);

                return ParseCommandModelResponse(response);
            }
            catch (Exception)
            {
                return MakeError($"Antigravity API error: {ex1.Message}");
            }
        }
    }

    // ── Process Detection (Windows-specific) ────────────────────────

    private record ProcessInfo(int Port, string CsrfToken);

    private async Task<ProcessInfo?> DetectProcessInfo(CancellationToken ct)
    {
        // Use PowerShell Get-CimInstance to list processes with command lines
        var result = await _cliHelper.ExecuteCommandAsync(
            "powershell.exe",
            "-NoProfile -Command \"Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'language_server' } | Select-Object ProcessId, CommandLine | Format-List\"",
            8000);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            return null;

        // Parse the output for CSRF token and port
        var lines = result.Output.Split('\n');
        string? commandLine = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("CommandLine", StringComparison.OrdinalIgnoreCase))
            {
                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx > 0)
                    commandLine = trimmed[(colonIdx + 1)..].Trim();
            }
        }

        if (string.IsNullOrEmpty(commandLine))
            return null;

        // Check it's actually an Antigravity process
        var lowerCmd = commandLine.ToLowerInvariant();
        if (!lowerCmd.Contains("antigravity") && !ProcessNames.Any(n => lowerCmd.Contains(n)))
            return null;

        // Extract CSRF token
        var csrfToken = ExtractFlag("--csrf_token", commandLine);
        if (string.IsNullOrEmpty(csrfToken))
            return null;

        // Extract API port (try --api_server_port first, then common ports)
        var portStr = ExtractFlag("--api_server_port", commandLine)
                   ?? ExtractFlag("--extension_server_port", commandLine);

        if (int.TryParse(portStr, out var port))
            return new ProcessInfo(port, csrfToken);

        // Try netstat to find listening ports for the process
        return null;
    }

    private static string? ExtractFlag(string flag, string commandLine)
    {
        var pattern = $@"{Regex.Escape(flag)}[=\s]+(\S+)";
        var match = Regex.Match(commandLine, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    // ── API Requests ────────────────────────────────────────────────

    private static readonly HttpClientHandler InsecureHandler = new()
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    };

    private static readonly HttpClient ApiClient = new(InsecureHandler)
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static async Task<string> MakeApiRequest(
        int port, string csrfToken, string path, object body, CancellationToken ct)
    {
        // Try HTTPS first, fall back to HTTP
        foreach (var scheme in new[] { "https", "http" })
        {
            try
            {
                var url = $"{scheme}://127.0.0.1:{port}{path}";
                var jsonBody = JsonSerializer.Serialize(body);

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                request.Headers.Add("Connect-Protocol-Version", "1");
                request.Headers.Add("X-Codeium-Csrf-Token", csrfToken);

                using var response = await ApiClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch when (scheme == "https")
            {
                // Fall through to HTTP
            }
        }

        throw new Exception("Could not connect to Antigravity language server.");
    }

    private static object DefaultRequestBody() => new
    {
        metadata = new
        {
            ideName = "antigravity",
            extensionName = "antigravity",
            ideVersion = "unknown",
            locale = "en"
        }
    };

    // ── Response Parsing ────────────────────────────────────────────

    private static ProviderUsageStatus ParseUserStatusResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Validate response code
        if (root.TryGetProperty("code", out var code))
        {
            var codeStr = code.ValueKind == JsonValueKind.Number
                ? code.GetInt32().ToString()
                : code.GetString() ?? "0";
            if (codeStr != "0" && !codeStr.Equals("ok", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"API error code: {codeStr}");
        }

        if (!root.TryGetProperty("userStatus", out var userStatus))
            throw new Exception("Missing userStatus in response");

        var email = userStatus.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null;

        string? planName = null;
        if (userStatus.TryGetProperty("planStatus", out var planStatus) &&
            planStatus.TryGetProperty("planInfo", out var planInfo))
        {
            planName = TryGetString(planInfo, "planDisplayName")
                    ?? TryGetString(planInfo, "displayName")
                    ?? TryGetString(planInfo, "productName")
                    ?? TryGetString(planInfo, "planName");
        }

        // Parse model quotas
        var models = new List<(string label, string modelId, double? remaining)>();
        if (userStatus.TryGetProperty("cascadeModelConfigData", out var configData) &&
            configData.TryGetProperty("clientModelConfigs", out var configs))
        {
            foreach (var config in configs.EnumerateArray())
            {
                var label = TryGetString(config, "label") ?? "unknown";
                var modelId = config.TryGetProperty("modelOrAlias", out var moa) &&
                              moa.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                double? remaining = null;
                if (config.TryGetProperty("quotaInfo", out var qi) &&
                    qi.TryGetProperty("remainingFraction", out var rf))
                {
                    remaining = rf.GetDouble();
                }
                if (remaining.HasValue)
                    models.Add((label, modelId, remaining));
            }
        }

        if (models.Count == 0)
            throw new Exception("No model quotas found");

        // Select display models: Claude (non-thinking) > Pro > Flash
        var sorted = models.OrderBy(m => m.remaining ?? 0).ToList();
        var primary = sorted.FirstOrDefault(m => m.label.Contains("claude", StringComparison.OrdinalIgnoreCase) &&
                                                  !m.label.Contains("thinking", StringComparison.OrdinalIgnoreCase));
        if (primary == default) primary = sorted[0];

        var secondary = sorted.FirstOrDefault(m => m != primary &&
            m.label.Contains("pro", StringComparison.OrdinalIgnoreCase));
        if (secondary == default) secondary = sorted.FirstOrDefault(m => m != primary);

        var sessionUsed = primary.remaining.HasValue ? 1.0 - primary.remaining.Value : 0.0;
        var weeklyUsed = secondary.remaining.HasValue ? 1.0 - secondary.remaining.Value : 0.0;

        var tooltipParts = new List<string> { "Antigravity" };
        tooltipParts.Add($"{primary.label}: {sessionUsed * 100:F1}% used");
        if (secondary != default)
            tooltipParts.Add($"{secondary.label}: {weeklyUsed * 100:F1}% used");
        tooltipParts.Add($"Models: {models.Count} tracked");
        if (planName != null) tooltipParts.Add($"Plan: {planName}");
        if (email != null) tooltipParts.Add($"Account: {email}");

        return new ProviderUsageStatus
        {
            SessionProgress = Math.Min(1.0, sessionUsed),
            WeeklyProgress = Math.Min(1.0, weeklyUsed),
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    private static ProviderUsageStatus ParseCommandModelResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("clientModelConfigs", out var configs))
            throw new Exception("No model configs in response");

        var models = new List<(string label, double remaining)>();
        foreach (var config in configs.EnumerateArray())
        {
            var label = TryGetString(config, "label") ?? "unknown";
            if (config.TryGetProperty("quotaInfo", out var qi) &&
                qi.TryGetProperty("remainingFraction", out var rf))
            {
                models.Add((label, rf.GetDouble()));
            }
        }

        if (models.Count == 0)
            return MakeError("No quota data from Antigravity.");

        var lowest = models.OrderBy(m => m.remaining).First();
        var used = 1.0 - lowest.remaining;

        return new ProviderUsageStatus
        {
            SessionProgress = Math.Min(1.0, used),
            WeeklyProgress = 0.0,
            IsError = false,
            TooltipText = $"Antigravity\n{lowest.label}: {used * 100:F1}% used\nModels: {models.Count} tracked"
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string? TryGetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static ProviderUsageStatus MakeError(string message) => new()
    {
        IsError = true,
        ErrorMessage = message,
        TooltipText = $"Antigravity: {message}"
    };
}
