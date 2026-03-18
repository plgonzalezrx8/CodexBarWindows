using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexBarWindows.Abstractions;
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
    private readonly ICommandRunner _commandRunner;
    private readonly SettingsService _settings;
    private readonly HttpClient _apiClient;

    private const string GetUserStatusPath   = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
    private const string GetModelConfigPath   = "/exa.language_server_pb.LanguageServerService/GetCommandModelConfigs";
    private const string UnleashPath = "/exa.language_server_pb.LanguageServerService/GetUnleashData";
    private static readonly string[] ProcessNames = ["language_server_windows", "language_server"];

    public string ProviderId => "antigravity";
    public string ProviderName => "Antigravity";
    public bool IsEnabled => _settings.CurrentSettings.EnabledProviders.GetValueOrDefault("antigravity", false);

    public AntigravityProvider(ICommandRunner commandRunner, SettingsService settings, HttpClient apiClient)
    {
        _commandRunner = commandRunner;
        _settings = settings;
        _apiClient = apiClient;
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

    internal record ProcessInfo(int Port, string CsrfToken);

    private async Task<ProcessInfo?> DetectProcessInfo(CancellationToken ct)
    {
        // Step 1: Find Antigravity language server process — get PID + CommandLine
        var result = await _commandRunner.ExecuteCommandAsync(
            "powershell.exe",
            "-NoProfile -Command \"Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*language_server*' } | ForEach-Object { \"$($_.ProcessId)|$($_.CommandLine)\" }\"",
            timeoutMilliseconds: 8000,
            cancellationToken: ct);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            return null;

        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sepIndex = line.IndexOf('|');
            if (sepIndex <= 0) continue;

            var pidStr = line[..sepIndex].Trim();
            var commandLine = line[(sepIndex + 1)..].Trim();

            if (!int.TryParse(pidStr, out var pid)) continue;

            var parsed = TryParseCommandLine(commandLine);
            if (parsed == null) continue;

            // Step 2: If we already have a port from command line, use it
            if (parsed.Port > 0)
                return parsed;

            // Step 3: Discover listening ports via Get-NetTCPConnection (like macOS lsof)
            var port = await DiscoverWorkingPort(pid, parsed.CsrfToken, ct);
            if (port > 0)
                return new ProcessInfo(port, parsed.CsrfToken);
        }

        return null;
    }

    private async Task<int> DiscoverWorkingPort(int pid, string csrfToken, CancellationToken ct)
    {
        var result = await _commandRunner.ExecuteCommandAsync(
            "powershell.exe",
            $"-NoProfile -Command \"Get-NetTCPConnection -OwningProcess {pid} -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort\"",
            timeoutMilliseconds: 5000,
            cancellationToken: ct);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            return 0;

        var ports = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p.Trim(), out var port) ? port : 0)
            .Where(p => p > 0)
            .OrderBy(p => p)
            .ToList();

        // Test each port to find the working API port
        foreach (var port in ports)
        {
            if (await TestPort(port, csrfToken, ct))
                return port;
        }

        return 0;
    }

    private async Task<bool> TestPort(int port, string csrfToken, CancellationToken ct)
    {
        foreach (var scheme in new[] { "https", "http" })
        {
            try
            {
                var url = $"{scheme}://127.0.0.1:{port}{UnleashPath}";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new { apiKeyRequired = true }),
                    Encoding.UTF8, "application/json");
                request.Headers.Add("Connect-Protocol-Version", "1");
                request.Headers.Add("X-Codeium-Csrf-Token", csrfToken);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(3000);
                using var response = await _apiClient.SendAsync(request, cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch { /* Try next scheme/port */ }
        }
        return false;
    }

    internal static ProcessInfo? TryParseCommandLine(string commandLine)
    {
        var lowerCmd = commandLine.ToLowerInvariant();

        // Must contain a known process name
        var hasProcessName = ProcessNames.Any(n => lowerCmd.Contains(n));
        if (!hasProcessName)
            return null;

        // Must be an Antigravity process (not Windsurf or generic Codeium)
        if (!IsAntigravityProcess(lowerCmd))
            return null;

        // Extract CSRF token (required)
        var csrfToken = ExtractFlag("--csrf_token", commandLine);
        if (string.IsNullOrEmpty(csrfToken))
            return null;

        // Try to extract API port (may not exist with --random_port)
        var portStr = ExtractFlag("--api_server_port", commandLine);
        int.TryParse(portStr, out var port);

        return new ProcessInfo(port, csrfToken);
    }

    internal static bool IsAntigravityProcess(string lowerCommandLine)
    {
        // Match: --app_data_dir antigravity
        if (lowerCommandLine.Contains("--app_data_dir") && lowerCommandLine.Contains("antigravity"))
            return true;
        // Match: path containing \antigravity\ or /antigravity/
        if (lowerCommandLine.Contains("\\antigravity\\") || lowerCommandLine.Contains("/antigravity/"))
            return true;
        return false;
    }

    internal static string? ExtractFlag(string flag, string commandLine)
    {
        var pattern = $@"{Regex.Escape(flag)}[=\s]+(\S+)";
        var match = Regex.Match(commandLine, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    // ── API Requests ────────────────────────────────────────────────

    private async Task<string> MakeApiRequest(
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

                using var response = await _apiClient.SendAsync(request, ct);
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

    internal static ProviderUsageStatus ParseUserStatusResponse(string json)
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
            ProviderId = "antigravity",
            ProviderName = "Antigravity",
            SessionProgress = Math.Min(1.0, sessionUsed),
            WeeklyProgress = Math.Min(1.0, weeklyUsed),
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    internal static ProviderUsageStatus ParseCommandModelResponse(string json)
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
            ProviderId = "antigravity",
            ProviderName = "Antigravity",
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
        ProviderId = "antigravity",
        ProviderName = "Antigravity",
        IsError = true,
        ErrorMessage = message,
        TooltipText = $"Antigravity: {message}"
    };
}
