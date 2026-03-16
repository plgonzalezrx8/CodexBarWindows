using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using CodexBarWindows.Models;
using CodexBarWindows.Services;

namespace CodexBarWindows.Providers;

/// <summary>
/// JetBrains AI provider — reads quota data from the local JetBrains IDE
/// AI Assistant configuration XML file. Detects the newest installed IDE
/// and parses the AIAssistantQuotaManager2 component for usage/limits.
/// </summary>
public class JetBrainsProvider : IProviderProbe
{
    private readonly SettingsService _settings;

    public string ProviderId => "jetbrains";
    public string ProviderName => "JetBrains AI";
    public bool IsEnabled => _settings.CurrentSettings.EnabledProviders.GetValueOrDefault("jetbrains", false);

    public JetBrainsProvider(SettingsService settings)
    {
        _settings = settings;
    }

    public Task<ProviderUsageStatus> FetchStatusAsync(CancellationToken ct)
    {
        try
        {
            var ide = DetectLatestIDE();
            if (ide == null)
                return Task.FromResult(MakeError("No JetBrains IDE with AI Assistant detected."));

            var quotaPath = Path.Combine(ide.ConfigPath, "options", "other.xml");
            if (!File.Exists(quotaPath))
                return Task.FromResult(MakeError($"AI quota file not found. Enable AI Assistant in {ide.DisplayName}."));

            var result = ParseQuotaFile(quotaPath, ide);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            return Task.FromResult(MakeError($"JetBrains error: {ex.Message}"));
        }
    }

    // ── IDE Detection ───────────────────────────────────────────────

    private record JetBrainsIDE(string ProductCode, string DisplayName, string ConfigPath);

    private static readonly Dictionary<string, string> ProductNames = new()
    {
        ["IntelliJIdea"] = "IntelliJ IDEA",
        ["WebStorm"] = "WebStorm",
        ["PyCharm"] = "PyCharm",
        ["PhpStorm"] = "PhpStorm",
        ["GoLand"] = "GoLand",
        ["Rider"] = "Rider",
        ["CLion"] = "CLion",
        ["DataGrip"] = "DataGrip",
        ["RubyMine"] = "RubyMine",
        ["DataSpell"] = "DataSpell",
        ["RustRover"] = "RustRover",
        ["Aqua"] = "Aqua"
    };

    private static JetBrainsIDE? DetectLatestIDE()
    {
        // JetBrains config on Windows: %APPDATA%\JetBrains\<Product><Version>
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var jetbrainsDir = Path.Combine(appData, "JetBrains");

        if (!Directory.Exists(jetbrainsDir))
            return null;

        JetBrainsIDE? latest = null;
        string? latestVersion = null;

        foreach (var dir in Directory.GetDirectories(jetbrainsDir))
        {
            var dirName = Path.GetFileName(dir);
            foreach (var (code, name) in ProductNames)
            {
                if (!dirName.StartsWith(code, StringComparison.OrdinalIgnoreCase)) continue;

                var version = dirName[code.Length..];
                if (string.IsNullOrEmpty(version)) continue;

                // Check if this IDE actually has AI Assistant config
                var quotaFile = Path.Combine(dir, "options", "other.xml");
                if (!File.Exists(quotaFile)) continue;

                if (latestVersion == null || string.Compare(version, latestVersion, StringComparison.Ordinal) > 0)
                {
                    latest = new JetBrainsIDE(code, name, dir);
                    latestVersion = version;
                }
            }
        }

        return latest;
    }

    // ── XML Parsing ─────────────────────────────────────────────────

    private static ProviderUsageStatus ParseQuotaFile(string path, JetBrainsIDE ide)
    {
        var xmlContent = File.ReadAllText(path);
        var doc = new XmlDocument();
        doc.LoadXml(xmlContent);

        // Find <component name="AIAssistantQuotaManager2">
        var components = doc.SelectNodes("//component[@name='AIAssistantQuotaManager2']/option");
        if (components == null || components.Count == 0)
            return MakeError($"No AI quota data in {ide.DisplayName}. Enable AI Assistant.");

        string? quotaInfoRaw = null;
        string? nextRefillRaw = null;

        foreach (XmlNode option in components)
        {
            var name = option.Attributes?["name"]?.Value;
            var value = option.Attributes?["value"]?.Value;
            if (name == "quotaInfo") quotaInfoRaw = value;
            if (name == "nextRefill") nextRefillRaw = value;
        }

        if (string.IsNullOrEmpty(quotaInfoRaw))
            return MakeError($"No quota info found in {ide.DisplayName}.");

        // Decode HTML entities
        var decoded = DecodeHtmlEntities(quotaInfoRaw);
        return ParseQuotaJson(decoded, nextRefillRaw, ide);
    }

    private static ProviderUsageStatus ParseQuotaJson(string json, string? refillJson, JetBrainsIDE ide)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        var used = root.TryGetProperty("current", out var c) ? ParseDoubleStr(c.GetString()) : 0;
        var maximum = root.TryGetProperty("maximum", out var m) ? ParseDoubleStr(m.GetString()) : 0;

        double usedPercent = maximum > 0 ? Math.Min(100, (used / maximum) * 100) : 0;

        // Parse refill info
        string? refillDesc = null;
        if (!string.IsNullOrEmpty(refillJson))
        {
            try
            {
                var refillDecoded = DecodeHtmlEntities(refillJson);
                using var refDoc = JsonDocument.Parse(refillDecoded);
                var refRoot = refDoc.RootElement;
                if (refRoot.TryGetProperty("next", out var next))
                {
                    var nextStr = next.GetString();
                    if (DateTime.TryParse(nextStr, out var nextDate))
                    {
                        var diff = nextDate - DateTime.UtcNow;
                        refillDesc = diff.TotalHours > 24
                            ? $"Resets in {diff.Days}d {diff.Hours}h"
                            : $"Resets in {(int)diff.TotalHours}h {diff.Minutes}m";
                    }
                }
            }
            catch { /* Best effort */ }
        }

        var tooltipParts = new List<string> { $"JetBrains AI ({ide.DisplayName})" };
        if (type != null) tooltipParts.Add($"Plan: {type}");
        tooltipParts.Add($"Usage: {used:F0}/{maximum:F0} ({usedPercent:F0}%)");
        if (refillDesc != null) tooltipParts.Add(refillDesc);

        return new ProviderUsageStatus
        {
            ProviderId = "jetbrains",
            ProviderName = "JetBrains AI",
            SessionProgress = Math.Min(1.0, usedPercent / 100.0),
            WeeklyProgress = 0.0,
            IsError = false,
            TooltipText = string.Join("\n", tooltipParts)
        };
    }

    private static double ParseDoubleStr(string? s) =>
        double.TryParse(s, out var d) ? d : 0;

    private static string DecodeHtmlEntities(string s) =>
        s.Replace("&#10;", "\n")
         .Replace("&quot;", "\"")
         .Replace("&amp;", "&")
         .Replace("&lt;", "<")
         .Replace("&gt;", ">")
         .Replace("&apos;", "'");

    private static ProviderUsageStatus MakeError(string message) => new()
    {
        ProviderId = "jetbrains",
        ProviderName = "JetBrains AI",
        IsError = true,
        ErrorMessage = message,
        TooltipText = $"JetBrains AI: {message}"
    };
}
