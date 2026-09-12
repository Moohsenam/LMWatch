using System.Globalization;
using System.Text.Json;

namespace ClaudeWatch.Orders;

/// <summary>What a rate lookup came back with.</summary>
public sealed class RateResult
{
    public bool Ok { get; init; }
    public long Toman { get; init; }
    public string Error { get; init; } = string.Empty;

    /// <summary>The number as the source gave it, before the unit was applied.</summary>
    public double Raw { get; init; }
}

/// <summary>
/// Reads one number out of a JSON document by a short path.
///
/// Supported: <c>a.b.c</c> for object keys, <c>a[2]</c> for an array index, and
/// <c>a[key=value]</c> for the first array element whose field equals a value.
/// That last form is what the Iranian rate feeds need, since they all return a
/// flat list of currencies rather than an object keyed by symbol.
///
/// It exists because these feeds come and go. When one dies, the fix is a new
/// URL and path typed into the admin panel, not a new build of anything.
/// </summary>
public static class JsonPick
{
    public static double? Extract(JsonElement root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return AsNumber(root);
        }

        var current = root;

        foreach (var step in Steps(path))
        {
            if (!Walk(current, step, out current))
            {
                return null;
            }
        }

        return AsNumber(current);
    }

    /// <summary>Splits "a.b[key=value].c" into its steps, keys and brackets alike.</summary>
    public static List<string> Steps(string path)
    {
        var steps = new List<string>();
        var token = string.Empty;
        var inBracket = false;

        foreach (var c in path)
        {
            switch (c)
            {
                case '[' when !inBracket:
                    if (token.Length > 0) { steps.Add(token); token = string.Empty; }
                    inBracket = true;
                    token = "[";
                    break;

                case ']' when inBracket:
                    steps.Add(token + "]");
                    token = string.Empty;
                    inBracket = false;
                    break;

                case '.' when !inBracket:
                    if (token.Length > 0) { steps.Add(token); token = string.Empty; }
                    break;

                default:
                    token += c;
                    break;
            }
        }

        if (token.Length > 0 && token != "[")
        {
            steps.Add(token);
        }

        return steps;
    }

    private static bool Walk(JsonElement from, string step, out JsonElement next)
    {
        next = default;

        if (step.StartsWith('[') && step.EndsWith(']'))
        {
            var inner = step[1..^1];

            if (from.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var equals = inner.IndexOf('=');
            if (equals > 0)
            {
                var field = inner[..equals].Trim();
                var wanted = inner[(equals + 1)..].Trim().Trim('"', '\'');

                foreach (var item in from.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) { continue; }
                    if (!item.TryGetProperty(field, out var value)) { continue; }

                    var text = value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : value.ToString();

                    if (string.Equals(text?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        next = item;
                        return true;
                    }
                }

                return false;
            }

            if (!int.TryParse(inner, out var index) || index < 0)
            {
                return false;
            }

            var seen = 0;
            foreach (var item in from.EnumerateArray())
            {
                if (seen++ == index)
                {
                    next = item;
                    return true;
                }
            }

            return false;
        }

        if (from.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Case-insensitive, because these feeds are not consistent about it.
        foreach (var property in from.EnumerateObject())
        {
            if (string.Equals(property.Name, step, StringComparison.OrdinalIgnoreCase))
            {
                next = property.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Numbers arrive as numbers, as "1,234,500", and as "۱٬۲۳۴٬۵۰۰". All three
    /// have to come out the same.
    /// </summary>
    public static double? AsNumber(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.GetDouble();
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return ParseNumber(element.GetString());
    }

    public static double? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var clean = string.Empty;

        foreach (var c in text)
        {
            if (c is >= '0' and <= '9') { clean += c; continue; }
            if (c is >= '۰' and <= '۹') { clean += (char)('0' + (c - '۰')); continue; } // Persian
            if (c is >= '٠' and <= '٩') { clean += (char)('0' + (c - '٠')); continue; } // Arabic
            if (c == '.') { clean += c; continue; }
            // Everything else — separators, currency words, spaces — is noise.
        }

        clean = clean.Trim('.');

        return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}

/// <summary>
/// Turns dollar prices into toman prices. Holds the rate, the markup and the
/// rounding, and keeps the last good rate so a feed going down does not blank
/// out the buy page.
/// </summary>
public sealed class PricingService : IDisposable
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private readonly Store _store;
    private readonly Timer _timer;

    public PricingService(Store store)
    {
        _store = store;

        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SafeChat/1.0");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

        // First run shortly after boot, then on the configured interval. The
        // interval is re-read each tick so a change in the panel takes effect
        // without a restart.
        _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));
    }

    public async Task<RateResult> FetchAsync(string url, string path, string unit)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new RateResult { Error = "no source url" };
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return new RateResult { Error = "source url is not http(s)" };
        }

        string body;

        try
        {
            using var response = await Http.GetAsync(uri);
            if (!response.IsSuccessStatusCode)
            {
                return new RateResult { Error = $"source returned {(int)response.StatusCode}" };
            }

            body = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            return new RateResult { Error = ex.Message };
        }

        double? picked;

        try
        {
            using var document = JsonDocument.Parse(body);
            picked = JsonPick.Extract(document.RootElement, path);
        }
        catch (JsonException)
        {
            // Some feeds answer with a bare number and no JSON at all.
            picked = JsonPick.ParseNumber(body);
        }

        if (picked is null || picked <= 0)
        {
            return new RateResult { Error = "no number at that path" };
        }

        var toman = ToToman(picked.Value, unit);

        return new RateResult { Ok = true, Toman = toman, Raw = picked.Value };
    }

    public static long ToToman(double value, string unit)
        => string.Equals(unit, "rial", StringComparison.OrdinalIgnoreCase)
            ? (long)Math.Round(value / 10)
            : (long)Math.Round(value);

    /// <summary>Refreshes the stored rate if it is due and the mode is automatic.</summary>
    public async Task<RateResult> RefreshAsync(bool force = false)
    {
        var pricing = _store.Config.Pricing;

        if (!string.Equals(pricing.RateMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return new RateResult { Ok = true, Toman = pricing.ManualRateToman };
        }

        if (!force && pricing.LastRateAt is { } last)
        {
            var due = last.AddMinutes(Math.Max(5, pricing.RefreshMinutes));
            if (DateTimeOffset.UtcNow < due)
            {
                return new RateResult { Ok = pricing.LastRateToman > 0, Toman = pricing.LastRateToman };
            }
        }

        var result = await FetchAsync(pricing.SourceUrl, pricing.SourcePath, pricing.SourceUnit);

        // A feed that answers with nonsense is worse than one that does not
        // answer at all, because nonsense reaches a customer as a real price.
        if (result.Ok && (result.Toman < pricing.MinRateToman || result.Toman > pricing.MaxRateToman))
        {
            result = new RateResult
            {
                Error = $"{result.Toman:N0} is outside the sanity range, ignored",
                Raw = result.Raw
            };
        }

        _store.SaveConfig(config =>
        {
            if (result.Ok)
            {
                config.Pricing.LastRateToman = result.Toman;
                config.Pricing.LastRateAt = DateTimeOffset.UtcNow;
                config.Pricing.LastRateError = string.Empty;
            }
            else
            {
                config.Pricing.LastRateError = result.Error;
                // LastRateAt is left alone on purpose: the stored rate keeps its
                // real age so the page can say how stale it is.
            }
        });

        return result;
    }

    /// <summary>The rate in force right now, and whether it can be trusted.</summary>
    public (long Toman, DateTimeOffset? At, bool Ready, bool Stale) CurrentRate()
    {
        var pricing = _store.Config.Pricing;

        if (string.Equals(pricing.RateMode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            var manual = pricing.ManualRateToman;
            return (manual, pricing.LastRateAt, manual > 0, false);
        }

        var age = pricing.LastRateAt is { } at ? DateTimeOffset.UtcNow - at : TimeSpan.MaxValue;
        var stale = age > TimeSpan.FromHours(Math.Max(1, pricing.RefreshMinutes / 60.0) * 6);

        return (pricing.LastRateToman, pricing.LastRateAt, pricing.LastRateToman > 0, stale);
    }

    /// <summary>Dollar price to the toman price a customer is quoted.</summary>
    public static long Quote(decimal usd, long rateToman, double markupPercent, long roundTo)
    {
        if (usd <= 0 || rateToman <= 0)
        {
            return 0;
        }

        var raw = (double)usd * rateToman * (1 + (markupPercent / 100.0));

        if (roundTo <= 0)
        {
            return (long)Math.Round(raw);
        }

        // Always up. Rounding a price down costs real money on every order.
        return (long)(Math.Ceiling(raw / roundTo) * roundTo);
    }

    /// <summary>
    /// The buy page, as the customer sees it. The markup and the dollar figures
    /// stay out of here unless the owner switches them on.
    /// </summary>
    public object PublicView()
    {
        var config = _store.Config;
        var pricing = config.Pricing;
        var (rate, at, ready, stale) = CurrentRate();

        var plans = config.Plans
            .Where(p => p.Enabled)
            .Select(p => new
            {
                key = p.Key,
                label = p.Label,
                labelFa = p.LabelFa,
                period = p.Period,
                service = string.IsNullOrWhiteSpace(p.Service) ? "claude" : p.Service,
                note = p.Note,
                noteFa = p.NoteFa,
                popular = p.Popular,
                usd = pricing.ShowUsd ? p.UsdPrice : (decimal?)null,
                toman = Quote(p.UsdPrice, rate, pricing.MarkupPercent, pricing.RoundToToman),
                variable = p.UsdPrice <= 0
            })
            .ToList();

        return new
        {
            ok = true,
            rateReady = ready,
            rateStale = stale,
            updatedAt = at,
            currency = "IRT",
            plans
        };
    }

    public void Dispose() => _timer.Dispose();
}
