using System.Net.Http;
using System.Text.Json;

namespace ClaudeWatch.Core;

/// <summary>One plan as the buy page shows it.</summary>
public sealed class PricedPlan
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string LabelFa { get; init; } = string.Empty;

    /// <summary>month, year, seat, or empty.</summary>
    public string Period { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;
    public string NoteFa { get; init; } = string.Empty;
    public bool Popular { get; init; }

    /// <summary>Null unless the owner chose to show dollar figures.</summary>
    public decimal? Usd { get; init; }

    public long Toman { get; init; }

    /// <summary>The amount is up to the customer, so there is no fixed price.</summary>
    public bool Variable { get; init; }

    public string Name(bool persian)
        => persian && !string.IsNullOrWhiteSpace(LabelFa) ? LabelFa : Label;

    public string Small(bool persian)
        => persian && !string.IsNullOrWhiteSpace(NoteFa) ? NoteFa : Note;
}

public sealed class PriceList
{
    public bool Ok { get; init; }
    public bool RateReady { get; init; }
    public bool RateStale { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public List<PricedPlan> Plans { get; init; } = new();

    /// <summary>Set when the list came off disk rather than the server.</summary>
    public bool FromCache { get; set; }

    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// Reads the buy page from the order server. The server does the converting, so
/// the rate, the markup and the dollar figures never have to live in the app
/// where a customer could read them out of a settings file.
/// </summary>
public sealed class PricingClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private readonly string _cacheFile;

    public PricingClient(string? cacheFile = null)
        => _cacheFile = cacheFile ?? Path.Combine(AppPaths.Root, "prices.json");

    public async Task<PriceList> LoadAsync(string baseUrl)
    {
        var address = OrdersClient.Normalize(baseUrl);

        if (string.IsNullOrWhiteSpace(address))
        {
            return Cached() ?? new PriceList { Error = "no_server" };
        }

        try
        {
            using var response = await Http.GetAsync(address.TrimEnd('/') + "/api/pricing");

            if (!response.IsSuccessStatusCode)
            {
                return Cached() ?? new PriceList { Error = $"http_{(int)response.StatusCode}" };
            }

            var body = await response.Content.ReadAsStringAsync();
            var list = Parse(body);

            if (list.Ok)
            {
                Save(body);
            }

            return list;
        }
        catch (Exception ex)
        {
            // Offline is the normal case for this app: the tunnel is down more
            // often than it is up. Last known prices beat an empty page.
            var cached = Cached();
            return cached ?? new PriceList { Error = ex.Message };
        }
    }

    public static PriceList Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var plans = new List<PricedPlan>();

            if (root.TryGetProperty("plans", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    plans.Add(new PricedPlan
                    {
                        Key = Str(item, "key"),
                        Label = Str(item, "label"),
                        LabelFa = Str(item, "labelFa"),
                        Period = Str(item, "period"),
                        Note = Str(item, "note"),
                        NoteFa = Str(item, "noteFa"),
                        Popular = Bool(item, "popular"),
                        Usd = Decimal(item, "usd"),
                        Toman = Long(item, "toman"),
                        Variable = Bool(item, "variable")
                    });
                }
            }

            return new PriceList
            {
                Ok = Bool(root, "ok"),
                RateReady = Bool(root, "rateReady"),
                RateStale = Bool(root, "rateStale"),
                UpdatedAt = Date(root, "updatedAt"),
                Plans = plans
            };
        }
        catch (JsonException)
        {
            return new PriceList { Error = "bad_json" };
        }
    }

    private PriceList? Cached()
    {
        try
        {
            if (!File.Exists(_cacheFile))
            {
                return null;
            }

            var list = Parse(File.ReadAllText(_cacheFile));
            if (!list.Ok)
            {
                return null;
            }

            list.FromCache = true;
            return list;
        }
        catch
        {
            return null;
        }
    }

    private void Save(string body)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile) ?? ".");
            File.WriteAllText(_cacheFile, body);
        }
        catch
        {
            // A cache that cannot be written is not worth an error.
        }
    }

    // ------------------------------------------------------------- reading

    private static string Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static long Long(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static decimal? Decimal(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : null;

    private static DateTimeOffset? Date(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    /// <summary>"۵٬۱۶۰٬۰۰۰" in Persian, "5,160,000" in English.</summary>
    public static string Money(long toman, bool persian)
    {
        if (toman <= 0)
        {
            return string.Empty;
        }

        var grouped = toman.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

        if (!persian)
        {
            return grouped;
        }

        // Done by hand rather than through fa-IR: .NET leaves Latin digits in
        // place for that culture, and a price is exactly where a Persian reader
        // notices.
        var persianDigits = string.Empty;

        foreach (var c in grouped)
        {
            persianDigits += c switch
            {
                >= '0' and <= '9' => (char)('۰' + (c - '0')),
                ',' => '٬',
                _ => c
            };
        }

        return persianDigits;
    }
}
