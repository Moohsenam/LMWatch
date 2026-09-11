using System.Text.Json.Serialization;

namespace ClaudeWatch.Orders;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OrderStatus
{
    /// <summary>Just arrived, not looked at yet.</summary>
    New,

    /// <summary>Checked and agreed with the customer.</summary>
    Confirmed,

    /// <summary>Waiting for the customer to settle up.</summary>
    AwaitingPayment,

    /// <summary>Paid on the card, subscription active.</summary>
    Paid,

    /// <summary>Finished and handed over.</summary>
    Done,

    /// <summary>Dropped.</summary>
    Cancelled
}

public sealed class OrderEvent
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public OrderStatus Status { get; set; }
    public string Note { get; set; } = string.Empty;
}

public sealed class Order
{
    public string Id { get; set; } = string.Empty;

    /// <summary>What the customer quotes back to ask about their order.</summary>
    public string Code { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string PlanKey { get; set; } = string.Empty;
    public string PlanLabel { get; set; } = string.Empty;
    public int Months { get; set; } = 1;

    public string AccountEmail { get; set; } = string.Empty;
    public string Contact { get; set; } = string.Empty;
    public string ContactKind { get; set; } = "telegram";
    public string Country { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    /// <summary>The customer's own statement that the account is eligible where they are.</summary>
    public bool EligibilityConfirmed { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.New;

    // Filled in by the owner as the order moves along.
    public string OwnerNote { get; set; } = string.Empty;
    public decimal? ChargedAmount { get; set; }
    public string ChargedCurrency { get; set; } = string.Empty;
    public decimal? CostAmount { get; set; }
    public string CostCurrency { get; set; } = "USD";
    public string PaymentRef { get; set; } = string.Empty;
    public DateTimeOffset? RenewsAt { get; set; }

    public List<OrderEvent> History { get; set; } = new();

    /// <summary>The slice a customer is allowed to see when they track their code.</summary>
    public object ToPublic() => new
    {
        code = Code,
        plan = PlanLabel,
        months = Months,
        status = Status.ToString(),
        createdAt = CreatedAt,
        updatedAt = UpdatedAt,
        renewsAt = RenewsAt,
        message = OwnerNote
    };
}

public sealed class Plan
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string LabelFa { get; set; } = string.Empty;
    public string PriceHint { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary>What Anthropic charges. Zero means the amount is up to the customer.</summary>
    public decimal UsdPrice { get; set; }

    /// <summary>month, year, seat, or empty for a one-off.</summary>
    public string Period { get; set; } = "month";

    public string Note { get; set; } = string.Empty;
    public string NoteFa { get; set; } = string.Empty;

    /// <summary>Highlighted on the buy page.</summary>
    public bool Popular { get; set; }
}

/// <summary>
/// How dollar prices become toman prices. Lives on the server so the panel is
/// the only place any of it is ever edited.
/// </summary>
public sealed class PricingConfig
{
    /// <summary>Added to every price. Never leaves the server.</summary>
    public double MarkupPercent { get; set; } = 15;

    /// <summary>Prices are rounded up to a multiple of this, so they read cleanly.</summary>
    public long RoundToToman { get; set; } = 10_000;

    /// <summary>auto (fetched from a feed) or manual (typed in the panel).</summary>
    public string RateMode { get; set; } = "auto";

    public long ManualRateToman { get; set; }

    public string SourceUrl { get; set; } = "https://api.priceto.day/v1/latest/irr/usd";

    /// <summary>Where the number sits in the response. See JsonPick.</summary>
    public string SourcePath { get; set; } = "price";

    /// <summary>toman or rial, whichever the feed reports in.</summary>
    public string SourceUnit { get; set; } = "toman";

    public int RefreshMinutes { get; set; } = 60;

    // A feed that answers with nonsense would otherwise quote nonsense prices.
    public long MinRateToman { get; set; } = 10_000;
    public long MaxRateToman { get; set; } = 10_000_000;

    /// <summary>Show the dollar figure next to the toman price. Off by default.</summary>
    public bool ShowUsd { get; set; }

    // Last good lookup, kept so a dead feed does not empty the buy page.
    public long LastRateToman { get; set; }
    public DateTimeOffset? LastRateAt { get; set; }
    public string LastRateError { get; set; } = string.Empty;
}

public sealed class ServiceConfig
{
    public string BusinessName { get; set; } = "Claude Watch";
    public string ContactLine { get; set; } = string.Empty;
    public string ContactUrl { get; set; } = string.Empty;

    /// <summary>Shown on the form so customers know what to expect.</summary>
    public string Notice { get; set; } = string.Empty;

    public List<Plan> Plans { get; set; } = new();

    public PricingConfig Pricing { get; set; } = new();

    // Auth material. Generated on first run, never shipped with the source.
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public string SessionSecret { get; set; } = string.Empty;

    public static ServiceConfig CreateDefault() => new()
    {
        BusinessName = "Claude Watch",
        ContactLine = string.Empty,
        ContactUrl = string.Empty,
        Notice = string.Empty,
        // Dollar figures as published by Anthropic. Checked 11 September 2026;
        // the panel is where they get corrected when they move.
        Plans = new List<Plan>
        {
            new()
            {
                Key = "pro", Label = "Claude Pro", LabelFa = "کلاد پرو",
                PriceHint = "$20 / month", UsdPrice = 20m, Period = "month", Popular = true
            },
            new()
            {
                Key = "pro-year", Label = "Claude Pro — yearly", LabelFa = "کلاد پرو — سالانه",
                PriceHint = "$200 / year", UsdPrice = 200m, Period = "year",
                Note = "Works out cheaper per month.", NoteFa = "ماهانه‌اش ارزان‌تر درمی‌آید."
            },
            new()
            {
                Key = "max5", Label = "Claude Max 5×", LabelFa = "کلاد مکس ۵×",
                PriceHint = "$100 / month", UsdPrice = 100m, Period = "month"
            },
            new()
            {
                Key = "max20", Label = "Claude Max 20×", LabelFa = "کلاد مکس ۲۰×",
                PriceHint = "$200 / month", UsdPrice = 200m, Period = "month"
            },
            new()
            {
                Key = "team", Label = "Claude Team", LabelFa = "کلاد تیم",
                PriceHint = "$25 / seat / month", UsdPrice = 25m, Period = "seat"
            },
            new()
            {
                Key = "api", Label = "API credit", LabelFa = "کردیت API",
                PriceHint = "any amount", UsdPrice = 0m, Period = string.Empty,
                Note = "Tell us the amount you want.", NoteFa = "مبلغ دلخواهتان را بگویید."
            }
        }
    };
}

// ---------------------------------------------------------------- requests

public sealed class OrderRequest
{
    public string? Plan { get; set; }
    public int Months { get; set; } = 1;
    public string? Email { get; set; }
    public string? Contact { get; set; }
    public string? ContactKind { get; set; }
    public string? Country { get; set; }
    public string? Note { get; set; }
    public bool Eligible { get; set; }

    /// <summary>Hidden field. A real person leaves it empty.</summary>
    public string? Website { get; set; }
}

public sealed class LoginRequest
{
    public string? Password { get; set; }
}

public sealed class PasswordChangeRequest
{
    public string? Current { get; set; }
    public string? Next { get; set; }
}

public sealed class OrderUpdate
{
    public string? Status { get; set; }
    public string? OwnerNote { get; set; }
    public decimal? ChargedAmount { get; set; }
    public string? ChargedCurrency { get; set; }
    public decimal? CostAmount { get; set; }
    public string? CostCurrency { get; set; }
    public string? PaymentRef { get; set; }
    public DateTimeOffset? RenewsAt { get; set; }
    public string? HistoryNote { get; set; }
}

public sealed class ConfigUpdate
{
    public string? BusinessName { get; set; }
    public string? ContactLine { get; set; }
    public string? ContactUrl { get; set; }
    public string? Notice { get; set; }
    public List<Plan>? Plans { get; set; }
    public PricingUpdate? Pricing { get; set; }
}

public sealed class PricingUpdate
{
    public double? MarkupPercent { get; set; }
    public long? RoundToToman { get; set; }
    public string? RateMode { get; set; }
    public long? ManualRateToman { get; set; }
    public string? SourceUrl { get; set; }
    public string? SourcePath { get; set; }
    public string? SourceUnit { get; set; }
    public int? RefreshMinutes { get; set; }
    public long? MinRateToman { get; set; }
    public long? MaxRateToman { get; set; }
    public bool? ShowUsd { get; set; }
}

public sealed class RateTestRequest
{
    public string? Url { get; set; }
    public string? Path { get; set; }
    public string? Unit { get; set; }
}
