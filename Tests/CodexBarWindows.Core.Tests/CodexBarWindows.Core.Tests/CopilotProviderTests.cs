using System.Net;
using System.Net.Http.Headers;
using CodexBarWindows.Providers;
using CodexBarWindows.Services;

namespace CodexBarWindows.Core.Tests;

public class CopilotProviderTests
{
    [Fact]
    public async Task Uses_codexbar_token_before_github_token_and_parses_usage()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var environment = new FakeEnvironmentService();
        environment.Variables["CODEXBAR_COPILOT_TOKEN"] = "preferred-token";
        environment.Variables["GITHUB_COPILOT_TOKEN"] = "fallback-token";

        AuthenticationHeaderValue? seenAuthorization = null;
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            seenAuthorization = request.Headers.Authorization;

            return request.RequestUri!.AbsolutePath.Equals("/user", StringComparison.OrdinalIgnoreCase)
                ? Json("""{"login":"copilot@example.com"}""")
                : Json("""[{"total_acceptances_count":3,"total_suggestions_count":6}]""");
        }));

        var provider = new CopilotProvider(settings, environment, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Equal("preferred-token", seenAuthorization?.Parameter);
        Assert.Contains("Account: copilot@example.com", status.TooltipText);
        Assert.Contains("Suggestions: 6", status.TooltipText);
        Assert.Contains("Acceptances: 3", status.TooltipText);
        Assert.Equal(0.5, status.SessionProgress, 3);
    }

    [Fact]
    public async Task Returns_error_when_no_token_is_available()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var environment = new FakeEnvironmentService();
        var client = new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called when no token exists.")));

        var provider = new CopilotProvider(settings, environment, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.True(status.IsError);
        Assert.Contains("No Copilot token", status.TooltipText);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
}
