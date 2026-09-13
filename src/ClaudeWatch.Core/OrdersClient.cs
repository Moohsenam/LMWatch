using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ClaudeWatch.Core;

public sealed class OrderPlan
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string LabelFa { get; init; } = string.Empty;
    public string PriceHint { get; init; } = string.Empty;

    /// <summary>claude or chatgpt. Older servers leave it empty, which reads as Claude.</summary>
    public string Service { get; init; } = string.Empty;

    public bool BelongsTo(string? serviceKey)
        => string.Equals(
            string.IsNullOrWhiteSpace(Service) ? "claude" : Service,
            string.IsNullOrWhiteSpace(serviceKey) ? "claude" : serviceKey,
            StringComparison.OrdinalIgnoreCase);

    public string Display(bool persian)
    {
        var name = persian && !string.IsNullOrWhiteSpace(LabelFa) ? LabelFa : Label;
        // The bidi isolate keeps "$20 / month" in one piece inside a Persian line.
        return string.IsNullOrWhiteSpace(PriceHint) ? name : $"{name} — ⁦{PriceHint}⁩";
    }

    public override string ToString() => Label;
}

public sealed class OrderService
{
    public string BusinessName { get; init; } = string.Empty;
    public string ContactLine { get; init; } = string.Empty;
    public string ContactUrl { get; init; } = string.Empty;
    public string Notice { get; init; } = string.Empty;
    public List<OrderPlan> Plans { get; init; } = new();
}

public sealed class OrderStatusView
{
    public string Code { get; init; } = string.Empty;
    public string Plan { get; init; } = string.Empty;
    public int Months { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class OrderDraft
{
    public string Plan { get; set; } = string.Empty;
    public int Months { get; set; } = 1;

    /// <summary>Who to ask for when calling. The server refuses an order without it.</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>claude or chatgpt. Filled in from whichever service is active.</summary>
    public string Service { get; set; } = "claude";

    public string Email { get; set; } = string.Empty;
    public string Contact { get; set; } = string.Empty;
    public string ContactKind { get; set; } = "telegram";
    public string Country { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public bool Eligible { get; set; }
}

public sealed class OrderResult
{
    public bool Ok { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
}

/// <summary>Talks to the orders service so the app can place and track orders itself.</summary>
public sealed class OrdersClient
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string Normalize(string baseUrl)
    {
        var text = (baseUrl ?? string.Empty).Trim().TrimEnd('/');

        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text;
        }

        return text;
    }

    public async Task<OrderService?> GetServiceAsync(string baseUrl, CancellationToken cancel = default)
    {
        var root = Normalize(baseUrl);
        if (root.Length == 0)
        {
            return null;
        }

        try
        {
            var body = await Client.GetStringAsync($"{root}/api/service", cancel).ConfigureAwait(false);
            return JsonSerializer.Deserialize<OrderService>(body, Json);
        }
        catch
        {
            return null;
        }
    }

    public async Task<OrderResult> PlaceAsync(string baseUrl, OrderDraft draft, CancellationToken cancel = default)
    {
        var root = Normalize(baseUrl);
        if (root.Length == 0)
        {
            return new OrderResult { Ok = false, Error = "no_server" };
        }

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                plan = draft.Plan,
                months = draft.Months,
                fullName = draft.FullName,
                service = draft.Service,
                email = draft.Email,
                contact = draft.Contact,
                contactKind = draft.ContactKind,
                country = draft.Country,
                note = draft.Note,
                eligible = draft.Eligible
            }, Json);

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await Client.PostAsync($"{root}/api/orders", content, cancel).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);

            using var document = JsonDocument.Parse(body);

            if (response.IsSuccessStatusCode && document.RootElement.TryGetProperty("code", out var code))
            {
                return new OrderResult { Ok = true, Code = code.GetString() ?? string.Empty };
            }

            var error = document.RootElement.TryGetProperty("error", out var reason)
                ? reason.GetString() ?? "failed"
                : "failed";

            return new OrderResult { Ok = false, Error = error };
        }
        catch (Exception ex)
        {
            return new OrderResult { Ok = false, Error = ex is TaskCanceledException ? "timeout" : "network" };
        }
    }

    public async Task<OrderStatusView?> TrackAsync(string baseUrl, string code, CancellationToken cancel = default)
    {
        var root = Normalize(baseUrl);
        if (root.Length == 0 || string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        try
        {
            using var response = await Client
                .GetAsync($"{root}/api/orders/{Uri.EscapeDataString(code.Trim())}", cancel)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            return JsonSerializer.Deserialize<OrderStatusView>(body, Json);
        }
        catch
        {
            return null;
        }
    }
}
