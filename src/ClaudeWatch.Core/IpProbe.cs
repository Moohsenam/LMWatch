using System.Net.Http;
using System.Text.Json;

namespace ClaudeWatch.Core;

public sealed class IpInfo
{
    public string Ip { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string Network { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public bool Ok { get; init; }
    public string Error { get; init; } = string.Empty;

    public string Where
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(City)) parts.Add(City);
            if (!string.IsNullOrWhiteSpace(Country)) parts.Add(Country);
            return string.Join(", ", parts);
        }
    }
}

/// <summary>
/// Asks the internet what address it sees. Three providers are tried in turn so
/// one being blocked or slow does not blank the panel.
/// </summary>
public sealed class IpProbe
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        client.DefaultRequestHeaders.Add("User-Agent", "ClaudeWatch/1.0");
        return client;
    }

    public async Task<IpInfo> LookupAsync(CancellationToken cancel = default)
    {
        var errors = new List<string>();

        foreach (var provider in Providers())
        {
            try
            {
                using var response = await Client.GetAsync(provider.Url, cancel).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{provider.Name}: HTTP {(int)response.StatusCode}");
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
                var info = provider.Parse(body);

                if (info is not null && !string.IsNullOrWhiteSpace(info.Ip))
                {
                    return info;
                }

                errors.Add($"{provider.Name}: no address in the answer");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{provider.Name}: {ex.Message}");
            }
        }

        return new IpInfo
        {
            Ok = false,
            Error = errors.Count > 0 ? errors[0] : "No provider answered."
        };
    }

    private sealed class Provider
    {
        public string Name { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
        public Func<string, IpInfo?> Parse { get; init; } = _ => null;
    }

    private static IEnumerable<Provider> Providers()
    {
        yield return new Provider
        {
            Name = "ipinfo.io",
            Url = "https://ipinfo.io/json",
            Parse = body => FromJson(body, "ipinfo.io", "ip", "country", "city", "org")
        };

        yield return new Provider
        {
            Name = "ifconfig.co",
            Url = "https://ifconfig.co/json",
            Parse = body => FromJson(body, "ifconfig.co", "ip", "country", "city", "asn_org")
        };

        yield return new Provider
        {
            Name = "ipapi.co",
            Url = "https://ipapi.co/json/",
            Parse = body => FromJson(body, "ipapi.co", "ip", "country_name", "city", "org")
        };
    }

    private static IpInfo? FromJson(string body, string source, string ipKey, string countryKey, string cityKey, string networkKey)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            return new IpInfo
            {
                Ip = Read(root, ipKey),
                Country = Read(root, countryKey),
                City = Read(root, cityKey),
                Network = Read(root, networkKey),
                Source = source,
                Ok = true
            };
        }
        catch
        {
            return null;
        }
    }

    private static string Read(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            _ => string.Empty
        };
    }
}
