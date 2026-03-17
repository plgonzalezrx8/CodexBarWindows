namespace CodexBarWindows.Abstractions;

public interface ICredentialStore
{
    void SaveCredential(string providerId, string secret, string? accountLabel = null);
    string? GetCredential(string providerId, string? accountLabel = null);
    void DeleteCredential(string providerId, string? accountLabel = null);
    IReadOnlyList<StoredCredentialInfo> ListAllCredentials();
    void CacheCookieHeader(string providerId, string cookieHeader, string sourceLabel);
    CachedCookieEntry? GetCachedCookieHeader(string providerId);
    void ClearCachedCookieHeader(string providerId);
}

public class StoredCredentialInfo
{
    public string TargetName { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public string? Comment { get; set; }
}

public class CachedCookieEntry
{
    public string CookieHeader { get; set; } = string.Empty;
    public DateTime StoredAt { get; set; }
    public string SourceLabel { get; set; } = string.Empty;
}
