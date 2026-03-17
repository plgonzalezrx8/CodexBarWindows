using System.Net;
using System.Net.Http.Headers;
using CodexBarWindows.Providers;
using CodexBarWindows.Services;

namespace CodexBarWindows.Core.Tests;

public class OpenRouterProviderTests
{
    [Fact]
    public async Task Uses_env_api_key_and_parses_usage()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var environment = new FakeEnvironmentService();
        environment.Variables["OPENROUTER_API_KEY"] = "or-test-key";

        AuthenticationHeaderValue? seenAuthorization = null;
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            seenAuthorization = request.Headers.Authorization;
            return Json("""{"data":{"label":"primary-key","usage":12.34,"limit":40,"limit_remaining":27.66}}""");
        }));

        var provider = new OpenRouterProvider(settings, environment, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Equal("Bearer", seenAuthorization?.Scheme);
        Assert.Equal("or-test-key", seenAuthorization?.Parameter);
        Assert.Contains("Key: primary-key", status.TooltipText);
        Assert.Contains("Usage: $12.34", status.TooltipText);
        Assert.Contains("Remaining: $27.66", status.TooltipText);
        Assert.Equal(12.34 / 40.0, status.SessionProgress, 3);
    }

    [Fact]
    public async Task Returns_error_when_api_key_is_missing()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var environment = new FakeEnvironmentService();
        var client = new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called when no key exists.")));

        var provider = new OpenRouterProvider(settings, environment, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.True(status.IsError);
        Assert.Contains("No OpenRouter API key", status.TooltipText);
    }

    [Fact]
    public async Task Returns_error_for_unexpected_response_format()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var environment = new FakeEnvironmentService();
        environment.Variables["OPENROUTER_API_KEY"] = "or-test-key";
        var client = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            }));

        var provider = new OpenRouterProvider(settings, environment, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.True(status.IsError);
        Assert.Contains("Unexpected OpenRouter response format", status.TooltipText);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
}
