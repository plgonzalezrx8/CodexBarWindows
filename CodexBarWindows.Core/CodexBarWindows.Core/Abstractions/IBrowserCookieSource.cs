namespace CodexBarWindows.Abstractions;

public interface IBrowserCookieSource
{
    string? GetCookieHeader(string domain);
}
