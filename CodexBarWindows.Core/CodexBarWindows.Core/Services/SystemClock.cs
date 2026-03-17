using CodexBarWindows.Abstractions;

namespace CodexBarWindows.Services;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime LocalNow => DateTime.Now;
}
