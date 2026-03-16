using Meziantou.Framework.Win32;

namespace CodexBarWindows.Services;

/// <summary>
/// Wraps Windows Credential Manager for secure storage of API tokens,
/// OAuth credentials, and cached cookie headers — replacing macOS Keychain.
/// All credentials are stored per-provider under the "CodexBar:" prefix.
/// </summary>
public class CredentialManagerService
{
    private const string CredentialPrefix = "CodexBar:";
    private const string DefaultCookieSourceLabel = "browser-auto";

    // ── Store / Update ──────────────────────────────────────────────

    /// <summary>
    /// Saves a credential (token, password, or cookie header) for the given
    /// provider and optional account label.
    /// </summary>
    public void SaveCredential(string providerId, string secret, string? accountLabel = null)
    {
        var targetName = BuildTargetName(providerId, accountLabel);
        CredentialManager.WriteCredential(
            applicationName: targetName,
            userName: accountLabel ?? providerId,
            secret: secret,
            comment: $"CodexBar credential for {providerId}",
            persistence: CredentialPersistence.LocalMachine);
    }

    // ── Retrieve ────────────────────────────────────────────────────

    /// <summary>
    /// Reads the stored secret for a provider, returning null when none exists.
    /// </summary>
    public string? GetCredential(string providerId, string? accountLabel = null)
    {
        var targetName = BuildTargetName(providerId, accountLabel);
        var credential = CredentialManager.ReadCredential(targetName);
        return credential?.Password;
    }

    // ── Delete ──────────────────────────────────────────────────────

    /// <summary>
    /// Removes a stored credential for the provider. No-op if not found.
    /// </summary>
    public void DeleteCredential(string providerId, string? accountLabel = null)
    {
        var targetName = BuildTargetName(providerId, accountLabel);
        try
        {
            CredentialManager.DeleteCredential(targetName);
        }
        catch
        {
            // Credential did not exist — nothing to do.
        }
    }

    // ── Enumeration ─────────────────────────────────────────────────

    /// <summary>
    /// Lists all CodexBar-managed credentials currently stored in Windows
    /// Credential Manager.  Useful for the Settings → Providers panel.
    /// </summary>
    public IReadOnlyList<StoredCredentialInfo> ListAllCredentials()
    {
        var result = new List<StoredCredentialInfo>();

        var all = CredentialManager.EnumerateCredentials();
        if (all == null) return result;

        foreach (var c in all)
        {
            if (c.ApplicationName?.StartsWith(CredentialPrefix, StringComparison.OrdinalIgnoreCase) == true)
            {
                result.Add(new StoredCredentialInfo
                {
                    TargetName = c.ApplicationName,
                    UserName = c.UserName,
                    Comment = c.Comment
                });
            }
        }

        return result;
    }

    // ── Cookie-specific helpers ─────────────────────────────────────

    /// <summary>Stores a cached cookie header for the provider.</summary>
    public void CacheCookieHeader(string providerId, string cookieHeader, string sourceLabel)
    {
        var normalizedSourceLabel = string.IsNullOrWhiteSpace(sourceLabel)
            ? DefaultCookieSourceLabel
            : sourceLabel;

        // Store the cookie value
        SaveCredential(providerId + ":cookie", cookieHeader, normalizedSourceLabel);
        // Store metadata (timestamp + source) as a separate credential
        var meta = $"{DateTime.UtcNow:O}|{normalizedSourceLabel}";
        SaveCredential(providerId + ":cookie-meta", meta, normalizedSourceLabel);
    }

    /// <summary>Retrieves a cached cookie header, or null if not present.</summary>
    public CachedCookieEntry? GetCachedCookieHeader(string providerId)
    {
        var sourceLabel = DefaultCookieSourceLabel;
        var meta = GetCredential(providerId + ":cookie-meta", sourceLabel)
            ?? GetCredential(providerId + ":cookie-meta");
        DateTime storedAt = DateTime.MinValue;

        if (!string.IsNullOrEmpty(meta))
        {
            var parts = meta.Split('|', 2);
            if (parts.Length == 2)
            {
                DateTime.TryParse(parts[0], out storedAt);
                sourceLabel = parts[1];
            }
        }

        var cookie = GetCredential(providerId + ":cookie", sourceLabel)
            ?? GetCredential(providerId + ":cookie");
        if (string.IsNullOrEmpty(cookie)) return null;

        return new CachedCookieEntry
        {
            CookieHeader = cookie,
            StoredAt = storedAt,
            SourceLabel = sourceLabel
        };
    }

    /// <summary>Clears a cached cookie header for the provider.</summary>
    public void ClearCachedCookieHeader(string providerId)
    {
        DeleteCredential(providerId + ":cookie", DefaultCookieSourceLabel);
        DeleteCredential(providerId + ":cookie-meta", DefaultCookieSourceLabel);
        DeleteCredential(providerId + ":cookie");
        DeleteCredential(providerId + ":cookie-meta");
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static string BuildTargetName(string providerId, string? accountLabel)
    {
        return accountLabel != null
            ? $"{CredentialPrefix}{providerId}:{accountLabel}"
            : $"{CredentialPrefix}{providerId}";
    }
}

// ── Supporting models ───────────────────────────────────────────────

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
