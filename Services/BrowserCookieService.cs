using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexBarWindows.Abstractions;
using Microsoft.Data.Sqlite;

namespace CodexBarWindows.Services;

/// <summary>
/// Extracts cookies from Chromium-based browsers (Chrome, Edge) on Windows
/// by reading the SQLite Cookies database and decrypting values via DPAPI +
/// AES-256-GCM (Chromium v80+ encryption scheme).
///
/// Also supports reading Firefox cookies (unencrypted SQLite).
/// </summary>
public class BrowserCookieService : IBrowserCookieSource
{
    // ── Browser Definitions ─────────────────────────────────────────

    public enum Browser
    {
        Chrome,
        Edge,
        Brave,
        Firefox
    }

    /// <summary>Default browser import order (highest-priority first).</summary>
    public static readonly Browser[] DefaultBrowserOrder = [Browser.Chrome, Browser.Edge, Browser.Brave, Browser.Firefox];

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>
    /// Attempts to extract cookies for a given domain from the first available
    /// browser in the import order.  Returns the combined "Cookie" header
    /// value, or null if no cookies were found.
    /// </summary>
    public string? GetCookieHeader(string domain)
    {
        return GetCookieHeader(domain, null);
    }

    public string? GetCookieHeader(string domain, IEnumerable<Browser>? browserOrder = null)
    {
        foreach (var browser in browserOrder ?? DefaultBrowserOrder)
        {
            try
            {
                var cookies = browser == Browser.Firefox
                    ? ReadFirefoxCookies(domain)
                    : ReadChromiumCookies(browser, domain);

                if (cookies != null && cookies.Count > 0)
                {
                    return string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BrowserCookieService] {browser} cookie read failed: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Reads cookies for a specific domain from a specific browser.
    /// </summary>
    public List<BrowserCookie>? GetCookiesForBrowser(Browser browser, string domain)
    {
        return browser == Browser.Firefox
            ? ReadFirefoxCookies(domain)
            : ReadChromiumCookies(browser, domain);
    }

    /// <summary>
    /// Returns the label string for the browser (for UI display).
    /// </summary>
    public static string GetBrowserLabel(Browser browser) => browser switch
    {
        Browser.Chrome => "Google Chrome",
        Browser.Edge => "Microsoft Edge",
        Browser.Brave => "Brave Browser",
        Browser.Firefox => "Mozilla Firefox",
        _ => browser.ToString()
    };

    /// <summary>
    /// Checks whether the cookie database for the given browser exists on
    /// disk — i.e. whether the browser is installed and has been run.
    /// </summary>
    public bool IsBrowserAvailable(Browser browser)
    {
        var dbPath = GetCookieDbPath(browser);
        return dbPath != null && File.Exists(dbPath);
    }

    // ── Chromium Cookies (Chrome / Edge / Brave) ────────────────────

    private List<BrowserCookie>? ReadChromiumCookies(Browser browser, string domain)
    {
        var dbPath = GetCookieDbPath(browser);
        if (dbPath == null || !File.Exists(dbPath)) return null;

        var encryptionKey = GetChromiumEncryptionKey(browser);
        if (encryptionKey == null) return null;

        // Chromium locks the cookie DB — copy to a temp file to avoid locking issues.
        var tempDb = Path.GetTempFileName();
        try
        {
            File.Copy(dbPath, tempDb, overwrite: true);

            // Also copy the WAL file if it exists for consistency.
            var walPath = dbPath + "-wal";
            if (File.Exists(walPath))
                File.Copy(walPath, tempDb + "-wal", overwrite: true);

            var shmPath = dbPath + "-shm";
            if (File.Exists(shmPath))
                File.Copy(shmPath, tempDb + "-shm", overwrite: true);

            var result = new List<BrowserCookie>();

            using var connection = new SqliteConnection($"Data Source={tempDb};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT host_key, name, encrypted_value, path, expires_utc, is_secure, is_httponly
                FROM cookies
                WHERE host_key LIKE @domain OR host_key LIKE @dotDomain";
            command.Parameters.AddWithValue("@domain", $"%{domain}%");
            command.Parameters.AddWithValue("@dotDomain", $"%.{domain}%");

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var encryptedValue = reader.GetFieldValue<byte[]>(2);
                var decryptedValue = DecryptChromiumCookieValue(encryptedValue, encryptionKey);
                if (decryptedValue == null) continue;

                result.Add(new BrowserCookie
                {
                    Host = reader.GetString(0),
                    Name = reader.GetString(1),
                    Value = decryptedValue,
                    Path = reader.GetString(3),
                    ExpiresUtc = reader.GetInt64(4),
                    IsSecure = reader.GetBoolean(5),
                    IsHttpOnly = reader.GetBoolean(6)
                });
            }

            return result;
        }
        finally
        {
            TryDeleteTempFiles(tempDb);
        }
    }

    // ── Chromium Encryption Key ─────────────────────────────────────

    private byte[]? GetChromiumEncryptionKey(Browser browser)
    {
        var localStatePath = GetLocalStatePath(browser);
        if (localStatePath == null || !File.Exists(localStatePath)) return null;

        try
        {
            var json = File.ReadAllText(localStatePath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt)) return null;
            if (!osCrypt.TryGetProperty("encrypted_key", out var encKeyProp)) return null;

            var base64Key = encKeyProp.GetString();
            if (string.IsNullOrEmpty(base64Key)) return null;

            var encKeyBytes = Convert.FromBase64String(base64Key);

            // Chromium prepends "DPAPI" (5 bytes) to the key before Base64-encoding.
            if (encKeyBytes.Length < 5) return null;
            var keyWithoutPrefix = encKeyBytes[5..];

            // Decrypt the AES key using DPAPI.
            return ProtectedData.Unprotect(keyWithoutPrefix, null, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BrowserCookieService] Failed to read encryption key for {browser}: {ex.Message}");
            return null;
        }
    }

    // ── Chromium Cookie Decryption (AES-256-GCM, v10+ prefix) ──────

    private static string? DecryptChromiumCookieValue(byte[] encryptedValue, byte[] key)
    {
        if (encryptedValue == null || encryptedValue.Length == 0) return null;

        // Chromium v80+ cookies are prefixed with "v10" or "v11" (3 bytes).
        // Bytes [3..15] = 12-byte nonce, bytes [15..] = ciphertext + 16-byte GCM tag.
        if (encryptedValue.Length > 3 && encryptedValue[0] == 'v' && (encryptedValue[1] == '1'))
        {
            if (encryptedValue.Length < 3 + 12 + 16) return null; // Too short.

            var nonce = encryptedValue[3..15];
            var ciphertextWithTag = encryptedValue[15..];

            // AES-GCM: last 16 bytes are the authentication tag.
            var tagSize = 16;
            var ciphertext = ciphertextWithTag[..^tagSize];
            var tag = ciphertextWithTag[^tagSize..];

            var plaintext = new byte[ciphertext.Length];
            using var aesGcm = new AesGcm(key, tagSize);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }

        // Legacy DPAPI-encrypted cookies (pre-v80) – rare on modern installs.
        try
        {
            var decrypted = ProtectedData.Unprotect(encryptedValue, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    // ── Firefox Cookies (unencrypted SQLite) ─────────────────────────

    private List<BrowserCookie>? ReadFirefoxCookies(string domain)
    {
        var profileDir = GetFirefoxDefaultProfileDir();
        if (profileDir == null) return null;

        var dbPath = Path.Combine(profileDir, "cookies.sqlite");
        if (!File.Exists(dbPath)) return null;

        var tempDb = Path.GetTempFileName();
        try
        {
            File.Copy(dbPath, tempDb, overwrite: true);
            var result = new List<BrowserCookie>();

            using var connection = new SqliteConnection($"Data Source={tempDb};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT host, name, value, path, expiry, isSecure, isHttpOnly
                FROM moz_cookies
                WHERE host LIKE @domain OR host LIKE @dotDomain";
            command.Parameters.AddWithValue("@domain", $"%{domain}%");
            command.Parameters.AddWithValue("@dotDomain", $"%.{domain}%");

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new BrowserCookie
                {
                    Host = reader.GetString(0),
                    Name = reader.GetString(1),
                    Value = reader.GetString(2),
                    Path = reader.GetString(3),
                    ExpiresUtc = reader.GetInt64(4),
                    IsSecure = reader.GetBoolean(5),
                    IsHttpOnly = reader.GetBoolean(6)
                });
            }

            return result;
        }
        finally
        {
            TryDeleteTempFiles(tempDb);
        }
    }

    // ── Path Helpers ────────────────────────────────────────────────

    private static string? GetCookieDbPath(Browser browser) => browser switch
    {
        Browser.Chrome => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Google\Chrome\User Data\Default\Cookies"),
        Browser.Edge => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Edge\User Data\Default\Cookies"),
        Browser.Brave => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"BraveSoftware\Brave-Browser\User Data\Default\Cookies"),
        Browser.Firefox => null, // Firefox uses a different path structure — handled separately.
        _ => null
    };

    private static string? GetLocalStatePath(Browser browser) => browser switch
    {
        Browser.Chrome => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Google\Chrome\User Data\Local State"),
        Browser.Edge => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Edge\User Data\Local State"),
        Browser.Brave => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"BraveSoftware\Brave-Browser\User Data\Local State"),
        _ => null
    };

    private static string? GetFirefoxDefaultProfileDir()
    {
        var mozPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Mozilla\Firefox\Profiles");

        if (!Directory.Exists(mozPath)) return null;

        // Use the first directory ending with ".default-release" (standard naming).
        var profiles = Directory.GetDirectories(mozPath, "*.default-release");
        if (profiles.Length > 0) return profiles[0];

        // Fall back to any ".default" profile.
        profiles = Directory.GetDirectories(mozPath, "*.default");
        return profiles.Length > 0 ? profiles[0] : null;
    }

    private static void TryDeleteTempFiles(string tempDb)
    {
        try { File.Delete(tempDb); } catch { }
        try { File.Delete(tempDb + "-wal"); } catch { }
        try { File.Delete(tempDb + "-shm"); } catch { }
    }
}

// ── Cookie Model ────────────────────────────────────────────────────

public class BrowserCookie
{
    public string Host { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Path { get; set; } = "/";
    public long ExpiresUtc { get; set; }
    public bool IsSecure { get; set; }
    public bool IsHttpOnly { get; set; }
}
