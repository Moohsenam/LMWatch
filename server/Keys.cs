using System.Security.Cryptography;
using System.Text;

namespace ClaudeWatch.Orders;

/// <summary>Where a key stands right now.</summary>
public enum KeyState
{
    /// <summary>Made, never activated. Free to hand to a customer.</summary>
    Unused,

    /// <summary>Activated and inside its window.</summary>
    Active,

    /// <summary>Its window has closed.</summary>
    Expired,

    /// <summary>Switched off by the owner before its time.</summary>
    Revoked
}

public sealed class LicenceKey
{
    /// <summary>The string the customer types, in SAFE-XXXX-XXXX-XXXX form.</summary>
    public string Code { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>How long the key runs for once it is first activated.</summary>
    public int Days { get; set; } = 30;

    /// <summary>Which service it unlocks: claude, chatgpt, or both.</summary>
    public string Service { get; set; } = "both";

    /// <summary>Set the first time someone activates it. Null while unused.</summary>
    public DateTimeOffset? ActivatedAt { get; set; }

    /// <summary>Counted from activation, not from creation, so stock does not rot.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>The one machine this key belongs to, as an opaque fingerprint.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Shown in the panel so a machine is recognisable at a glance.</summary>
    public string DeviceName { get; set; } = string.Empty;

    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>How many times the app has checked in. Useful for spotting sharing.</summary>
    public int Checks { get; set; }

    public bool Revoked { get; set; }

    /// <summary>Who it was sold to. Free text, the owner's own note.</summary>
    public string Customer { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    /// <summary>The order this key was issued against, if any.</summary>
    public string OrderCode { get; set; } = string.Empty;

    public KeyState State(DateTimeOffset now)
    {
        if (Revoked) { return KeyState.Revoked; }
        if (ActivatedAt is null) { return KeyState.Unused; }
        if (ExpiresAt is { } ends && now >= ends) { return KeyState.Expired; }
        return KeyState.Active;
    }

    public int DaysLeft(DateTimeOffset now)
        => ExpiresAt is { } ends ? Math.Max(0, (int)Math.Ceiling((ends - now).TotalDays)) : Days;

    /// <summary>What the panel shows.</summary>
    public object ToAdmin(DateTimeOffset now) => new
    {
        code = Code,
        state = State(now).ToString(),
        service = Service,
        days = Days,
        daysLeft = DaysLeft(now),
        createdAt = CreatedAt,
        activatedAt = ActivatedAt,
        expiresAt = ExpiresAt,
        deviceId = DeviceId,
        deviceName = DeviceName,
        lastSeenAt = LastSeenAt,
        checks = Checks,
        customer = Customer,
        note = Note,
        orderCode = OrderCode
    };
}

/// <summary>What the app is told when it activates or checks in.</summary>
public sealed class LicenceAnswer
{
    public bool Ok { get; init; }

    /// <summary>unknown_key, wrong_device, expired, revoked, or empty when fine.</summary>
    public string Error { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;
    public string Service { get; init; } = "both";
    public DateTimeOffset? ExpiresAt { get; init; }
    public int DaysLeft { get; init; }

    public object ToJson() => new
    {
        ok = Ok,
        error = Error,
        state = State,
        service = Service,
        expiresAt = ExpiresAt,
        daysLeft = DaysLeft
    };
}

public sealed class KeyRequest
{
    public string? Code { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
}

public sealed class KeyMakeRequest
{
    public int Count { get; set; } = 1;
    public int Days { get; set; } = 30;
    public string? Service { get; set; }
    public string? Customer { get; set; }
    public string? Note { get; set; }
    public string? OrderCode { get; set; }
}

public sealed class KeyEditRequest
{
    public string? Customer { get; set; }
    public string? Note { get; set; }
    public bool? Revoked { get; set; }

    /// <summary>Unbind the machine so the customer can activate somewhere else.</summary>
    public bool? ReleaseDevice { get; set; }

    /// <summary>Push the end date out by this many days.</summary>
    public int? AddDays { get; set; }
}

/// <summary>
/// Makes and checks licence keys.
///
/// A key is bound to the first machine that activates it and runs for a fixed
/// number of days from that moment, so unsold stock does not age. Binding is to
/// an opaque fingerprint the app sends; no hardware detail is stored here.
/// </summary>
public static class Keys
{
    // No 0/O/1/I/5/S: these get read over the phone and typed by hand.
    private const string Alphabet = "ABCDEFGHJKMNPQRTUVWXYZ2346789";

    public static string NewCode()
    {
        var groups = new string[3];

        for (var g = 0; g < groups.Length; g++)
        {
            var chars = new char[4];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }

            groups[g] = new string(chars);
        }

        return "SAFE-" + string.Join("-", groups);
    }

    /// <summary>
    /// Accepts what a human typed and returns the canonical form: upper case,
    /// separators normalised, and the SAFE- prefix put back if they dropped it.
    /// </summary>
    public static string Normalize(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return string.Empty;
        }

        var kept = new StringBuilder();

        foreach (var c in typed.Trim().ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                kept.Append(c);
            }
        }

        var body = kept.ToString();

        if (body.StartsWith("SAFE", StringComparison.Ordinal))
        {
            body = body[4..];
        }

        if (body.Length != 12)
        {
            // Not a shape we recognise. Hand it back so the lookup simply misses.
            return "SAFE-" + body;
        }

        return $"SAFE-{body[..4]}-{body[4..8]}-{body[8..]}";
    }

    public static bool LooksLikeCode(string? typed)
    {
        var normalized = Normalize(typed);
        return normalized.Length == 19 && normalized.StartsWith("SAFE-", StringComparison.Ordinal);
    }

    /// <summary>
    /// A device fingerprint the app sends, reduced to something short and
    /// non-reversible. The raw value never reaches disk.
    /// </summary>
    public static string Fingerprint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw.Trim()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
