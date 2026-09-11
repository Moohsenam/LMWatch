using System.Net.Http;
using System.Text.Json;

namespace ClaudeWatch.Core;

public sealed class UsageDay
{
    public DateOnly Day { get; init; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostUsd { get; set; }

    public long TotalTokens => InputTokens + CachedInputTokens + CacheCreationTokens + OutputTokens;
}

public sealed class UsageModel
{
    public string Model { get; init; } = string.Empty;
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens => InputTokens + OutputTokens;
}

public sealed class UsageReport
{
    public bool Ok { get; init; }
    public string Error { get; init; } = string.Empty;
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public int Days { get; init; }

    public List<UsageDay> Series { get; init; } = new();
    public List<UsageModel> Models { get; init; } = new();

    public long TotalTokens => Series.Sum(d => d.TotalTokens);
    public long TotalInput => Series.Sum(d => d.InputTokens + d.CachedInputTokens + d.CacheCreationTokens);
    public long TotalOutput => Series.Sum(d => d.OutputTokens);
    public decimal TotalCostUsd => Series.Sum(d => d.CostUsd);
    public bool CostAvailable { get; set; }
}

/// <summary>
/// Reads token and cost figures from Anthropic's Usage and Cost API.
///
/// Both endpoints want an Admin API key (sk-ant-admin…), which belongs to an API
/// organisation. A Pro or Max subscription on claude.ai has no such key and no
/// public usage endpoint, so for those accounts this simply reports that there
/// is nothing to read.
/// </summary>
public sealed class UsageClient
{
    private const string UsageEndpoint = "https://api.anthropic.com/v1/organizations/usage_report/messages";
    private const string CostEndpoint = "https://api.anthropic.com/v1/organizations/cost_report";
    private const string ApiVersion = "2023-06-01";

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static bool LooksLikeAdminKey(string key)
        => !string.IsNullOrWhiteSpace(key) && key.Trim().StartsWith("sk-ant-admin", StringComparison.OrdinalIgnoreCase);

    public async Task<UsageReport> FetchAsync(string adminKey, int days = 30, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(adminKey))
        {
            return new UsageReport { Ok = false, Error = "no_key", Days = days };
        }

        var ending = DateTimeOffset.UtcNow.Date.AddDays(1);
        var starting = ending.AddDays(-days);
        var buckets = new Dictionary<DateOnly, UsageDay>();
        var models = new Dictionary<string, UsageModel>(StringComparer.OrdinalIgnoreCase);

        var usageUrl = $"{UsageEndpoint}?starting_at={Iso(starting)}&ending_at={Iso(ending)}" +
                       "&bucket_width=1d&group_by[]=model&limit=31";

        try
        {
            var usage = await GetJsonAsync(usageUrl, adminKey, cancel).ConfigureAwait(false);

            if (usage.Error is not null)
            {
                return new UsageReport { Ok = false, Error = usage.Error, Days = days };
            }

            ReadUsage(usage.Document!, buckets, models);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UsageReport { Ok = false, Error = ex.Message, Days = days };
        }

        var report = new UsageReport
        {
            Ok = true,
            Days = days,
            Series = buckets.Values.OrderBy(d => d.Day).ToList(),
            Models = models.Values.OrderByDescending(m => m.TotalTokens).ToList()
        };

        // Costs are a separate endpoint and may be unavailable on some plans;
        // a failure there must not lose the token figures we already have.
        try
        {
            var costUrl = $"{CostEndpoint}?starting_at={Iso(starting)}&ending_at={Iso(ending)}&limit=31";
            var cost = await GetJsonAsync(costUrl, adminKey, cancel).ConfigureAwait(false);

            if (cost.Error is null && cost.Document is not null)
            {
                report.CostAvailable = ReadCost(cost.Document, report.Series);
            }
        }
        catch
        {
            report.CostAvailable = false;
        }

        return report;
    }

    private static string Iso(DateTimeOffset value)
        => Uri.EscapeDataString(value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));

    private async Task<(JsonDocument? Document, string? Error)> GetJsonAsync(string url, string key, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-api-key", key.Trim());
        request.Headers.Add("anthropic-version", ApiVersion);

        using var response = await Client.SendAsync(request, cancel).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = (int)response.StatusCode switch
            {
                401 => "unauthorized",
                403 => "not_admin_key",
                404 => "not_found",
                429 => "rate_limited",
                _ => $"http_{(int)response.StatusCode}"
            };

            return (null, error);
        }

        return (JsonDocument.Parse(body), null);
    }

    private static void ReadUsage(JsonDocument document, Dictionary<DateOnly, UsageDay> buckets, Dictionary<string, UsageModel> models)
    {
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var bucket in data.EnumerateArray())
        {
            var day = ReadDay(bucket);

            if (!buckets.TryGetValue(day, out var entry))
            {
                entry = new UsageDay { Day = day };
                buckets[day] = entry;
            }

            if (!bucket.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var result in results.EnumerateArray())
            {
                var input = Number(result, "uncached_input_tokens");
                var cached = Number(result, "cache_read_input_tokens");
                var creation = CacheCreation(result);
                var output = Number(result, "output_tokens");

                entry.InputTokens += input;
                entry.CachedInputTokens += cached;
                entry.CacheCreationTokens += creation;
                entry.OutputTokens += output;

                var modelName = String(result, "model");
                if (string.IsNullOrWhiteSpace(modelName))
                {
                    continue;
                }

                if (!models.TryGetValue(modelName, out var model))
                {
                    model = new UsageModel { Model = modelName };
                    models[modelName] = model;
                }

                model.InputTokens += input + cached + creation;
                model.OutputTokens += output;
            }
        }
    }

    private static bool ReadCost(JsonDocument document, List<UsageDay> series)
    {
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var byDay = series.ToDictionary(d => d.Day);
        var found = false;

        foreach (var bucket in data.EnumerateArray())
        {
            var day = ReadDay(bucket);

            if (!byDay.TryGetValue(day, out var entry))
            {
                entry = new UsageDay { Day = day };
                byDay[day] = entry;
                series.Add(entry);
            }

            if (!bucket.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("amount", out var amount))
                {
                    continue;
                }

                var raw = amount.ValueKind == JsonValueKind.String
                    ? amount.GetString()
                    : amount.ToString();

                if (decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    // The API reports amounts in cents.
                    entry.CostUsd += value / 100m;
                    found = true;
                }
            }
        }

        series.Sort((a, b) => a.Day.CompareTo(b.Day));
        return found;
    }

    private static DateOnly ReadDay(JsonElement bucket)
    {
        var start = String(bucket, "starting_at");
        return DateTimeOffset.TryParse(start, out var parsed)
            ? DateOnly.FromDateTime(parsed.UtcDateTime)
            : DateOnly.FromDateTime(DateTime.UtcNow);
    }

    private static long CacheCreation(JsonElement result)
    {
        // Cache creation is reported either as a flat number or split by lifetime.
        if (result.TryGetProperty("cache_creation", out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            long total = 0;
            foreach (var property in nested.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var part))
                {
                    total += part;
                }
            }

            return total;
        }

        return Number(result, "cache_creation_input_tokens");
    }

    private static long Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var parsed)
            ? parsed
            : 0;

    private static string String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
