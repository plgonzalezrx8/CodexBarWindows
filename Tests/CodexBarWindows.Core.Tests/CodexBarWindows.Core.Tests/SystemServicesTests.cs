using CodexBarWindows.Services;

namespace CodexBarWindows.Core.Tests;

public class SystemServicesTests
{
    [Fact]
    public void SystemClock_delegates_to_DateTime()
    {
        var clock = new SystemClock();

        var utcBefore = DateTime.UtcNow;
        var utcNow = clock.UtcNow;
        var utcAfter = DateTime.UtcNow;

        Assert.Equal(DateTimeKind.Utc, utcNow.Kind);
        Assert.InRange(utcNow, utcBefore.AddSeconds(-1), utcAfter.AddSeconds(1));

        var localBefore = DateTime.Now;
        var localNow = clock.LocalNow;
        var localAfter = DateTime.Now;

        Assert.Equal(DateTimeKind.Local, localNow.Kind);
        Assert.InRange(localNow, localBefore.AddSeconds(-1), localAfter.AddSeconds(1));
    }

    [Fact]
    public void SystemEnvironmentService_delegates_to_environment_api()
    {
        var service = new SystemEnvironmentService();
        var variableName = $"CODEXBAR_TEST_{Guid.NewGuid():N}";
        var variableValue = $"value-{Guid.NewGuid():N}";

        try
        {
            Environment.SetEnvironmentVariable(variableName, variableValue);

            Assert.Equal(variableValue, service.GetEnvironmentVariable(variableName));
            Assert.Equal(
                Environment.GetEnvironmentVariable(variableName),
                service.GetEnvironmentVariable(variableName));
            Assert.Equal(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                service.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }
}
