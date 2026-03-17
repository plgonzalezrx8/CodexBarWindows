using System.Text.Json;
using System.Globalization;
using CodexBarWindows.Providers;
using CodexBarWindows.Services;

namespace CodexBarWindows.Core.Tests;

public class ProviderParsingTests
{
    [Fact]
    public void Provider_registry_is_deterministic_and_unique()
    {
        var ids = ProviderDescriptorRegistry.All.Select(descriptor => descriptor.Id).ToList();

        Assert.NotEmpty(ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(ids, ProviderDescriptorRegistry.All.Select(descriptor => descriptor.Id).ToList());
    }

    [Fact]
    public void Codex_usage_snapshot_maps_to_status()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "codex-usage.json")));
        var usage = CodexProvider.ParseUsageSnapshot(doc.RootElement);
        var status = CodexProvider.CreateStatus(usage);

        Assert.False(status.IsError);
        Assert.Equal("codex", status.ProviderId);
        Assert.Equal(0.42, status.SessionProgress, 3);
        Assert.Contains("Credits: 12.50", status.TooltipText);
    }

    [Fact]
    public void Claude_cookie_helpers_extract_session_key()
    {
        var normalized = ClaudeProvider.NormalizeCookieHeader("foo=bar; sessionKey=abc123; another=value");

        Assert.Equal("sessionKey=abc123", normalized);
        Assert.Equal("abc123", ClaudeProvider.ExtractSessionKey(normalized));
    }

    [Fact]
    public void Antigravity_response_parses_usage()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "antigravity-user-status.json"));
        var status = AntigravityProvider.ParseUserStatusResponse(json);

        Assert.False(status.IsError);
        Assert.Contains("Plan: Pro", status.TooltipText);
        Assert.True(status.SessionProgress > 0.5);
    }

    [Fact]
    public void Kiro_output_parses_usage()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "kiro-usage.txt"));
        var status = KiroProvider.ParseUsageOutput(text);

        Assert.False(status.IsError);
        Assert.Contains("KIRO PRO", status.TooltipText);
        Assert.True(status.SessionProgress > 0.6);
    }

    [Fact]
    public void Kiro_output_parses_decimal_values_with_invariant_culture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

            var output = """
                Plan: KIRO PRO
                █████ 65%
                (32.5 of 50 covered in plan)
                """;

            var status = KiroProvider.ParseUsageOutput(output);

            Assert.False(status.IsError);
            Assert.Contains("Credits:", status.TooltipText);
            Assert.Equal(0.65, status.SessionProgress, 3);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void OpenRouter_response_parses_usage()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "openrouter-auth-key.json"));
        var status = OpenRouterProvider.ParseResponse(json);

        Assert.False(status.IsError);
        Assert.Contains("primary-key", status.TooltipText);
        Assert.Equal(12.34 / 40.0, status.SessionProgress, 3);
    }

    [Fact]
    public void JetBrains_quota_json_parses_usage()
    {
        var status = JetBrainsProvider.ParseQuotaJson("{\"type\":\"trial\",\"current\":\"25\",\"maximum\":\"100\"}", null, "Rider");

        Assert.False(status.IsError);
        Assert.Contains("Rider", status.TooltipText);
        Assert.Equal(0.25, status.SessionProgress, 3);
    }

    [Fact]
    public async Task JetBrains_provider_selects_highest_semantic_version()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        settings.CurrentSettings.EnabledProviders["jetbrains"] = true;
        var env = new FakeEnvironmentService();
        var appData = Path.Combine(paths.AppDataDirectory, "appdata");
        env.Folders[Environment.SpecialFolder.ApplicationData] = appData;
        Directory.CreateDirectory(Path.Combine(appData, "JetBrains", "Rider2024.9", "options"));
        Directory.CreateDirectory(Path.Combine(appData, "JetBrains", "Rider2024.10", "options"));

        await File.WriteAllTextAsync(
            Path.Combine(appData, "JetBrains", "Rider2024.9", "options", "other.xml"),
            """<application><component name="AIAssistantQuotaManager2"><option name="quotaInfo" value="{&quot;type&quot;:&quot;trial&quot;,&quot;current&quot;:&quot;9&quot;,&quot;maximum&quot;:&quot;100&quot;}" /></component></application>""");
        await File.WriteAllTextAsync(
            Path.Combine(appData, "JetBrains", "Rider2024.10", "options", "other.xml"),
            """<application><component name="AIAssistantQuotaManager2"><option name="quotaInfo" value="{&quot;type&quot;:&quot;trial&quot;,&quot;current&quot;:&quot;10&quot;,&quot;maximum&quot;:&quot;100&quot;}" /></component></application>""");

        var provider = new JetBrainsProvider(settings, env);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Contains("10/100", status.TooltipText);
    }

    [Fact]
    public async Task JetBrains_provider_prefers_multi_part_version_over_lexicographically_larger_string()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        settings.CurrentSettings.EnabledProviders["jetbrains"] = true;
        var env = new FakeEnvironmentService();
        var appData = Path.Combine(paths.AppDataDirectory, "appdata");
        env.Folders[Environment.SpecialFolder.ApplicationData] = appData;
        Directory.CreateDirectory(Path.Combine(appData, "JetBrains", "Rider2024.2.9", "options"));
        Directory.CreateDirectory(Path.Combine(appData, "JetBrains", "Rider2024.10.1", "options"));

        await File.WriteAllTextAsync(
            Path.Combine(appData, "JetBrains", "Rider2024.2.9", "options", "other.xml"),
            """<application><component name="AIAssistantQuotaManager2"><option name="quotaInfo" value="{&quot;type&quot;:&quot;trial&quot;,&quot;current&quot;:&quot;9&quot;,&quot;maximum&quot;:&quot;100&quot;}" /></component></application>""");
        await File.WriteAllTextAsync(
            Path.Combine(appData, "JetBrains", "Rider2024.10.1", "options", "other.xml"),
            """<application><component name="AIAssistantQuotaManager2"><option name="quotaInfo" value="{&quot;type&quot;:&quot;trial&quot;,&quot;current&quot;:&quot;10&quot;,&quot;maximum&quot;:&quot;100&quot;}" /></component></application>""");

        var provider = new JetBrainsProvider(settings, env);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Contains("10/100", status.TooltipText);
    }
}
