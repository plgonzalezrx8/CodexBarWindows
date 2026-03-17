using System.Net;
using System.Text;
using CodexBarWindows.Abstractions;
using CodexBarWindows.Providers;
using CodexBarWindows.Services;

namespace CodexBarWindows.Core.Tests;

public class AntigravityProviderTests
{
    [Fact]
    public async Task FetchStatusAsync_parses_plan_account_and_model_usage()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var commandRunner = new FakeCommandRunner
        {
            Handler = (_, _, _, _) => new CommandResult(
                0,
                """
                ProcessId : 1234
                CommandLine : C:\Program Files\Antigravity\language_server_windows.exe --csrf_token token-123 --api_server_port 4567
                """,
                string.Empty)
        };

        string? seenToken = null;
        string? seenPath = null;
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            seenToken = request.Headers.GetValues("X-Codeium-Csrf-Token").Single();
            seenPath = request.RequestUri!.AbsolutePath;
            return Json("""
                {
                  "code": 0,
                  "userStatus": {
                    "email": "dev@example.com",
                    "planStatus": {
                      "planInfo": {
                        "planDisplayName": "Pro"
                      }
                    },
                    "cascadeModelConfigData": {
                      "clientModelConfigs": [
                        {
                          "label": "Claude Sonnet",
                          "modelOrAlias": { "model": "claude-sonnet-4" },
                          "quotaInfo": { "remainingFraction": 0.2 }
                        },
                        {
                          "label": "Pro",
                          "modelOrAlias": { "model": "pro" },
                          "quotaInfo": { "remainingFraction": 0.4 }
                        }
                      ]
                    }
                  }
                }
                """);
        }));

        var provider = new AntigravityProvider(commandRunner, settings, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Equal("token-123", seenToken);
        Assert.Equal("/exa.language_server_pb.LanguageServerService/GetUserStatus", seenPath);
        Assert.Contains("Plan: Pro", status.TooltipText);
        Assert.Contains("Account: dev@example.com", status.TooltipText);
        Assert.Contains("Claude Sonnet: 80.0% used", status.TooltipText);
        Assert.Contains("Pro: 60.0% used", status.TooltipText);
        Assert.Equal(0.8, status.SessionProgress, 3);
        Assert.Equal(0.6, status.WeeklyProgress, 3);
    }

    [Fact]
    public async Task FetchStatusAsync_returns_error_when_process_is_missing()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var commandRunner = new FakeCommandRunner
        {
            Handler = (_, _, _, _) => new CommandResult(0, string.Empty, string.Empty)
        };
        var client = new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called when process detection fails.")));

        var provider = new AntigravityProvider(commandRunner, settings, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.True(status.IsError);
        Assert.Contains("language server not detected", status.TooltipText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchStatusAsync_falls_back_to_command_model_response_when_user_status_fails()
    {
        using var paths = new TestAppDataPaths();
        var settings = new SettingsService(paths);
        var commandRunner = new FakeCommandRunner
        {
            Handler = (_, _, _, _) => new CommandResult(
                0,
                """
                ProcessId : 1234
                CommandLine : C:\Program Files\Antigravity\language_server_windows.exe --csrf_token token-456 --api_server_port 4567
                """,
                string.Empty)
        };

        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/exa.language_server_pb.LanguageServerService/GetUserStatus" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                "/exa.language_server_pb.LanguageServerService/GetCommandModelConfigs" => Json("""
                    {
                      "clientModelConfigs": [
                        {
                          "label": "Flash",
                          "quotaInfo": { "remainingFraction": 0.2 }
                        },
                        {
                          "label": "Claude",
                          "quotaInfo": { "remainingFraction": 0.6 }
                        }
                      ]
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected path: {request.RequestUri.AbsolutePath}")
            };
        }));

        var provider = new AntigravityProvider(commandRunner, settings, client);

        var status = await provider.FetchStatusAsync(CancellationToken.None);

        Assert.False(status.IsError);
        Assert.Contains("Models: 2 tracked", status.TooltipText);
        Assert.Contains("Flash: 80.0% used", status.TooltipText);
        Assert.Equal(0.8, status.SessionProgress, 3);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
