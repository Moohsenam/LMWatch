using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeWatch.Orders;

/// <summary>
/// One admin, one password. The password is stored as a PBKDF2 hash, the session
/// is a signed token in an HttpOnly cookie, and both the login and the public
/// order form are rate limited per address.
/// </summary>
public sealed class Auth
{
    public const string CookieName = "cw_admin";

    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly Store _store;

    public Auth(Store store) => _store = store;

    // ------------------------------------------------------------- password

    public static (string Hash, string Salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public bool VerifyPassword(string password)
    {
        var config = _store.Config;

        if (string.IsNullOrEmpty(config.PasswordHash) || string.IsNullOrEmpty(config.PasswordSalt))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(config.PasswordSalt);
            var expected = Convert.FromBase64String(config.PasswordHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, expected.Length);

            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Generates a password on first run so the service is never left open.</summary>
    public string EnsurePassword()
    {
        var config = _store.Config;

        if (!string.IsNullOrEmpty(config.PasswordHash) && !string.IsNullOrEmpty(config.SessionSecret))
        {
            return string.Empty;
        }

        var generated = string.Empty;

        _store.SaveConfig(c =>
        {
            if (string.IsNullOrEmpty(c.SessionSecret))
            {
                c.SessionSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            }

            if (string.IsNullOrEmpty(c.PasswordHash))
            {
                generated = ReadableSecret();
                var (hash, salt) = HashPassword(generated);
                c.PasswordHash = hash;
                c.PasswordSalt = salt;
            }
        });

        return generated;
    }

    public void SetPassword(string password)
    {
        var (hash, salt) = HashPassword(password);

        _store.SaveConfig(c =>
        {
            c.PasswordHash = hash;
            c.PasswordSalt = salt;
            // Changing the password ends every session that is already open.
            c.SessionSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        });
    }

    private static string ReadableSecret()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        var builder = new StringBuilder();

        for (var i = 0; i < 20; i++)
        {
            if (i > 0 && i % 5 == 0)
            {
                builder.Append('-');
            }

            builder.Append(alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]);
        }

        return builder.ToString();
    }

    // -------------------------------------------------------------- session

    public string IssueToken(TimeSpan lifetime)
    {
        var expires = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        var payload = $"admin.{expires}";
        return $"{payload}.{Sign(payload)}";
    }

    public bool ValidateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != "admin")
        {
            return false;
        }

        var payload = $"{parts[0]}.{parts[1]}";
        var expected = Sign(payload);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[2])))
        {
            return false;
        }

        return long.TryParse(parts[1], out var expires)
               && DateTimeOffset.FromUnixTimeSeconds(expires) > DateTimeOffset.UtcNow;
    }

    private string Sign(string payload)
    {
        var secret = _store.Config.SessionSecret;
        var key = string.IsNullOrEmpty(secret) ? new byte[32] : Convert.FromBase64String(secret);
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}

/// <summary>A sliding window counter per key, held in memory. Restarting clears it.</summary>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _hits = new();

    public bool Allow(string key, int limit, TimeSpan window)
    {
        var now = DateTimeOffset.UtcNow;
        var entries = _hits.GetOrAdd(key, _ => new List<DateTimeOffset>());

        lock (entries)
        {
            entries.RemoveAll(t => now - t > window);

            if (entries.Count >= limit)
            {
                return false;
            }

            entries.Add(now);
            return true;
        }
    }

    public void Sweep()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var pair in _hits)
        {
            lock (pair.Value)
            {
                pair.Value.RemoveAll(t => now - t > TimeSpan.FromHours(2));
                if (pair.Value.Count == 0)
                {
                    _hits.TryRemove(pair.Key, out _);
                }
            }
        }
    }
}
