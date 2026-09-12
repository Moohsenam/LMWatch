using System.Text;
using ClaudeWatch.Orders;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

var dataRoot = Environment.GetEnvironmentVariable("CW_DATA")
               ?? Path.Combine(AppContext.BaseDirectory, "data");
var urls = Environment.GetEnvironmentVariable("CW_URLS") ?? "http://127.0.0.1:5080";

var store = new Store(dataRoot);
var auth = new Auth(store);
var limiter = new RateLimiter();
var pricing = new PricingService(store);

builder.Services.AddSingleton(store);
builder.Services.AddSingleton(auth);
builder.Services.AddSingleton(limiter);
builder.Services.AddSingleton(pricing);

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 64 * 1024;
    options.AddServerHeader = false;
});

builder.Logging.AddSimpleConsole(options => options.SingleLine = true);

var app = builder.Build();

// A password exists from the first run onwards; it is printed once, here.
var generated = auth.EnsurePassword();
if (!string.IsNullOrEmpty(generated))
{
    var noticeFile = Path.Combine(dataRoot, "FIRST-RUN-PASSWORD.txt");
    File.WriteAllText(noticeFile,
        $"Claude Watch orders — admin password{Environment.NewLine}{generated}{Environment.NewLine}" +
        $"Change it in the admin panel, then delete this file.{Environment.NewLine}");

    Console.WriteLine();
    Console.WriteLine("  ┌───────────────────────────────────────────────┐");
    Console.WriteLine("  │  First run. Admin password:                   │");
    Console.WriteLine($"  │  {generated,-43}│");
    Console.WriteLine("  │  Also saved to FIRST-RUN-PASSWORD.txt         │");
    Console.WriteLine("  └───────────────────────────────────────────────┘");
    Console.WriteLine();
}

// ------------------------------------------------------------------ basics

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "same-origin";
    headers["X-Frame-Options"] = "DENY";
    headers["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; form-action 'self'; frame-ancestors 'none'";
    await next();
});

var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(webRoot))
{
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = new PhysicalFileProvider(webRoot)
    });

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(webRoot),
        OnPrepareResponse = ctx =>
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate"
    });
}

string ClientKey(HttpContext context)
{
    var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
    if (!string.IsNullOrWhiteSpace(forwarded))
    {
        return forwarded.Split(',')[0].Trim();
    }

    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

bool IsAdmin(HttpContext context)
    => auth.ValidateToken(context.Request.Cookies[Auth.CookieName]);

/// Cookie sessions need a second signal that the request came from our own page.
bool HasCsrfHeader(HttpContext context)
    => context.Request.Headers.TryGetValue("X-CW", out var value) && value == "1";

/// Returns null when the caller may proceed, or the response to send instead.
IResult? RequireAdmin(HttpContext context, bool writing = false)
{
    if (!IsAdmin(context))
    {
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    }

    if (writing && !HasCsrfHeader(context))
    {
        return Results.Json(new { error = "bad_request" }, statusCode: 400);
    }

    return null;
}

// ------------------------------------------------------------------ public

app.MapGet("/api/health", () => Results.Json(new { ok = true, time = DateTimeOffset.UtcNow }));

app.MapGet("/api/service", () =>
{
    var config = store.Config;
    return Results.Json(new
    {
        businessName = config.BusinessName,
        contactLine = config.ContactLine,
        contactUrl = config.ContactUrl,
        notice = config.Notice,
        plans = config.Plans.Where(p => p.Enabled)
            .Select(p => new { p.Key, p.Label, p.LabelFa, p.PriceHint })
    });
});

// The buy page. Deliberately thin: names, periods and one toman figure each.
// No rate, no dollar price, no markup, unless the owner turns that on.
app.MapGet("/api/pricing", () => Results.Json(pricing.PublicView()));

// ------------------------------------------------------------------- keys
//
// Activation and the periodic re-check are the same call. The app sends the
// code and a device fingerprint; it gets back how long it has. Rate limited,
// because this endpoint is the one a guesser would hammer.

app.MapPost("/api/licence", (HttpContext context, KeyRequest request) =>
{
    if (!limiter.Allow("licence:" + ClientKey(context), 20, TimeSpan.FromMinutes(5)))
    {
        return Results.Json(new { ok = false, error = "slow_down" }, statusCode: 429);
    }

    if (!Keys.LooksLikeCode(request.Code))
    {
        return Results.Json(new { ok = false, error = "bad_shape" }, statusCode: 400);
    }

    var answer = store.CheckKey(request.Code, request.DeviceId, request.DeviceName);

    if (!answer.Ok)
    {
        app.Logger.LogInformation("Licence check refused: {Error}", answer.Error);
    }

    return Results.Json(answer.ToJson());
});

// How long a fresh install protects before a key is needed. The app asks once
// and remembers, so a customer who never reaches the server still gets a trial.
app.MapGet("/api/licence/terms", () => Results.Json(new
{
    trialDays = store.Config.TrialDays,
    keysRequired = store.Config.RequireKey
}));

app.MapPost("/api/orders", async (HttpContext context, OrderRequest request) =>
{
    if (!limiter.Allow("order:" + ClientKey(context), 6, TimeSpan.FromHours(1)))
    {
        return Results.Json(new { error = "too_many" }, statusCode: 429);
    }

    // Bots fill every field they find, including the one that is hidden.
    if (!string.IsNullOrWhiteSpace(request.Website))
    {
        await Task.Delay(400);
        return Results.Json(new { error = "invalid" }, statusCode: 400);
    }

    var plan = store.Config.Plans.FirstOrDefault(p =>
        p.Enabled && string.Equals(p.Key, request.Plan, StringComparison.OrdinalIgnoreCase));

    if (plan is null)
    {
        return Results.Json(new { error = "unknown_plan" }, statusCode: 400);
    }

    var email = Text.Clip(request.Email, 200);
    if (!email.Contains('@') || email.Length < 5)
    {
        return Results.Json(new { error = "bad_email" }, statusCode: 400);
    }

    var contact = Text.Clip(request.Contact, 200);
    if (contact.Length < 3)
    {
        return Results.Json(new { error = "bad_contact" }, statusCode: 400);
    }

    if (!request.Eligible)
    {
        return Results.Json(new { error = "eligibility_required" }, statusCode: 400);
    }

    var fullName = Text.Clip(request.FullName, 120);
    if (fullName.Length < 2)
    {
        return Results.Json(new { error = "bad_name" }, statusCode: 400);
    }

    var order = store.Create(new Order
    {
        PlanKey = plan.Key,
        PlanLabel = plan.Label,
        Months = Math.Clamp(request.Months, 1, 12),
        Service = string.Equals(request.Service, "chatgpt", StringComparison.OrdinalIgnoreCase) ? "chatgpt" : "claude",
        FullName = fullName,
        AccountEmail = email,
        Contact = contact,
        ContactKind = Text.Clip(request.ContactKind, 20),
        Country = Text.Clip(request.Country, 80),
        Note = Text.Clip(request.Note, 1000),
        EligibilityConfirmed = true,
        Status = OrderStatus.New
    });

    app.Logger.LogInformation("New order {Code} for {Plan}", order.Code, plan.Key);
    return Results.Json(new { code = order.Code });
});

app.MapGet("/api/orders/{code}", (HttpContext context, string code) =>
{
    if (!limiter.Allow("track:" + ClientKey(context), 60, TimeSpan.FromMinutes(10)))
    {
        return Results.Json(new { error = "too_many" }, statusCode: 429);
    }

    var order = store.ByCode(Text.Clip(code, 20));
    return order is null
        ? Results.Json(new { error = "not_found" }, statusCode: 404)
        : Results.Json(order.ToPublic());
});

// ------------------------------------------------------------------- admin

app.MapPost("/api/admin/login", (HttpContext context, LoginRequest request) =>
{
    var key = "login:" + ClientKey(context);

    if (!limiter.Allow(key, 10, TimeSpan.FromMinutes(15)))
    {
        return Results.Json(new { error = "too_many" }, statusCode: 429);
    }

    if (string.IsNullOrEmpty(request.Password) || !auth.VerifyPassword(request.Password))
    {
        return Results.Json(new { error = "bad_password" }, statusCode: 401);
    }

    context.Response.Cookies.Append(Auth.CookieName, auth.IssueToken(TimeSpan.FromDays(14)), new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = context.Request.IsHttps,
        Path = "/",
        MaxAge = TimeSpan.FromDays(14)
    });

    return Results.Json(new { ok = true });
});

app.MapPost("/api/admin/logout", (HttpContext context) =>
{
    context.Response.Cookies.Delete(Auth.CookieName);
    return Results.Json(new { ok = true });
});

app.MapGet("/api/admin/me", (HttpContext context)
    => Results.Json(new { signedIn = IsAdmin(context) }));

app.MapGet("/api/admin/orders", (HttpContext context, string? status, string? q, int? skip, int? take) =>
{
    var guard = RequireAdmin(context);
    if (guard is not null)
    {
        return guard;
    }

    var (items, total) = store.Search(status, q, Math.Max(0, skip ?? 0), Math.Clamp(take ?? 50, 1, 200));
    return Results.Json(new { items, total, summary = store.Summary() });
});

app.MapGet("/api/admin/orders/{id}", (HttpContext context, string id) =>
{
    var guard = RequireAdmin(context);
    if (guard is not null)
    {
        return guard;
    }

    var order = store.ById(id);
    return order is null ? Results.NotFound() : Results.Json(order);
});

app.MapPatch("/api/admin/orders/{id}", (HttpContext context, string id, OrderUpdate change) =>
{
    var guard = RequireAdmin(context, writing: true);
    if (guard is not null)
    {
        return guard;
    }

    var order = store.Update(id, change);
    return order is null ? Results.NotFound() : Results.Json(order);
});

app.MapDelete("/api/admin/orders/{id}", (HttpContext context, string id) =>
{
    var guard = RequireAdmin(context, writing: true);
    if (guard is not null)
    {
        return guard;
    }

    return store.Delete(id) ? Results.Json(new { ok = true }) : Results.NotFound();
});

app.MapGet("/api/admin/export.csv", (HttpContext context) =>
{
    var guard = RequireAdmin(context);
    if (guard is not null)
    {
        return guard;
    }

    var csv = store.ExportCsv();
    var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
    return Results.File(bytes, "text/csv", $"orders-{DateTime.UtcNow:yyyy-MM-dd}.csv");
});

app.MapGet("/api/admin/config", (HttpContext context) =>
{
    var guard = RequireAdmin(context);
    if (guard is not null)
    {
        return guard;
    }

    var config = store.Config;
    return Results.Json(new
    {
        config.BusinessName,
        config.ContactLine,
        config.ContactUrl,
        config.Notice,
        config.Plans,
        config.TrialDays,
        config.RequireKey
    });
});

app.MapPut("/api/admin/config", (HttpContext context, ConfigUpdate update) =>
{
    var guard = RequireAdmin(context, writing: true);
    if (guard is not null)
    {
        return guard;
    }

    store.SaveConfig(config =>
    {
        if (update.BusinessName is not null) config.BusinessName = Text.Clip(update.BusinessName, 80);
        if (update.ContactLine is not null) config.ContactLine = Text.Clip(update.ContactLine, 200);
        if (update.ContactUrl is not null) config.ContactUrl = Text.Clip(update.ContactUrl, 300);
        if (update.Notice is not null) config.Notice = Text.Clip(update.Notice, 600);
        if (update.TrialDays is { } trial) config.TrialDays = Math.Clamp(trial, 0, 90);
        if (update.RequireKey is { } requireKey) config.RequireKey = requireKey;

        if (update.Plans is not null)
        {
            config.Plans = update.Plans
                .Where(p => !string.IsNullOrWhiteSpace(p.Key))
                .Take(20)
                .Select(p => new Plan
                {
                    Key = Text.Clip(p.Key, 30),
                    Label = Text.Clip(p.Label, 80),
                    LabelFa = Text.Clip(p.LabelFa, 80),
                    PriceHint = Text.Clip(p.PriceHint, 60),
                    Enabled = p.Enabled,
                    UsdPrice = Math.Clamp(p.UsdPrice, 0m, 100_000m),
                    Period = Text.Clip(p.Period, 20),
                    Note = Text.Clip(p.Note, 200),
                    NoteFa = Text.Clip(p.NoteFa, 200),
                    Popular = p.Popular
                })
                .ToList();
        }

        if (update.Pricing is { } money)
        {
            var target = config.Pricing;

            if (money.MarkupPercent is { } markup) target.MarkupPercent = Math.Clamp(markup, 0, 300);
            if (money.RoundToToman is { } round) target.RoundToToman = Math.Clamp(round, 0, 1_000_000);
            if (money.ManualRateToman is { } manual) target.ManualRateToman = Math.Clamp(manual, 0, 100_000_000);
            if (money.RefreshMinutes is { } minutes) target.RefreshMinutes = Math.Clamp(minutes, 5, 1440);
            if (money.MinRateToman is { } min) target.MinRateToman = Math.Clamp(min, 1, 100_000_000);
            if (money.MaxRateToman is { } max) target.MaxRateToman = Math.Clamp(max, 1, 100_000_000);
            if (money.ShowUsd is { } showUsd) target.ShowUsd = showUsd;

            if (money.RateMode is not null)
            {
                target.RateMode = money.RateMode.Equals("manual", StringComparison.OrdinalIgnoreCase)
                    ? "manual"
                    : "auto";
            }

            if (money.SourceUnit is not null)
            {
                target.SourceUnit = money.SourceUnit.Equals("rial", StringComparison.OrdinalIgnoreCase)
                    ? "rial"
                    : "toman";
            }

            if (money.SourceUrl is not null) target.SourceUrl = Text.Clip(money.SourceUrl, 400);
            if (money.SourcePath is not null) target.SourcePath = Text.Clip(money.SourcePath, 200);

            if (target.MinRateToman > target.MaxRateToman)
            {
                (target.MinRateToman, target.MaxRateToman) = (target.MaxRateToman, target.MinRateToman);
            }
        }
    });

    return Results.Json(new { ok = true });
});

app.MapPost("/api/admin/password", (HttpContext context, PasswordChangeRequest request) =>
{
    var guard = RequireAdmin(context, writing: true);
    if (guard is not null)
    {
        return guard;
    }

    if (string.IsNullOrEmpty(request.Current) || !auth.VerifyPassword(request.Current))
    {
        return Results.Json(new { error = "bad_password" }, statusCode: 401);
    }

    if (string.IsNullOrWhiteSpace(request.Next) || request.Next.Trim().Length < 10)
    {
        return Results.Json(new { error = "too_short" }, statusCode: 400);
    }

    auth.SetPassword(request.Next.Trim());
    context.Response.Cookies.Delete(Auth.CookieName);
    return Results.Json(new { ok = true });
});

// --------------------------------------------------------------- admin keys

app.MapGet("/api/admin/keys", (HttpContext context, string? state, string? q) =>
{
    var guard = RequireAdmin(context);
    if (guard is not null)
    {
        return guard;
    }

    var now = DateTimeOffset.UtcNow;
    IEnumerable<LicenceKey> keys = store.AllKeys();

    if (!string.IsNullOrWhiteSpace(state) && Enum.TryParse<KeyState>(state, true, out var wanted))
    {
        keys = keys.Where(k => k.State(now) == wanted);
    }

    if (!string.IsNullOrWhiteSpace(q))
    {
        var needle = q.Trim();
        keys = keys.Where(k =>
            k.Code.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            k.Customer.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            k.Note.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            k.OrderCode.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            k.DeviceName.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    return Results.Json(new
    {
        summary = store.KeySummary(),
        items = keys.Take(500).Select(k => k.ToAdmin(now))
    });
});

app.MapPost("/api/admin/keys", (HttpContext context, KeyMakeRequest request) =>
{
    var guard = RequireAdmin(context, writing: true);
    if (guard is not null)
    {
        return guard;
    }

    var count = Math.Clamp(request.Count, 1, 200);
    var days = Math.Clamp(request.Days, 1, 3650);

    var service = request.Service?.ToLowerInvariant() switch
    {
        "claude" => "claude",
        "chatgpt" => "chatgpt",
        _ => "both"
    };

    var made = store.MakeKeys(
        count, days, service,
        Text.Clip(request.Customer, 120),
        Text.Clip(request.Note, 300),
        Text.Clip(request.OrderCode, 30));

    app.Logger.LogInformation("Made {Count} key(s) for {Days} days", made.Count, days);

    return Results.Json(new { ok = true, codes = made.Select(k => k.Code) });
});

app.MapPatch("/api/admin/keys/{code}", (HttpContext context, string code, KeyEditRequest change) =>
{
    var guard = RequireAdmin(context, writing: true);
    if (guard is not null)
    {
        return guard;
    }

    var key = store.EditKey(code, change);
    return key is null
        ? Results.Json(new { error = "not_found" }, statusCode: 404)
        : Results.Json(key.ToAdmin(DateTimeOffset.UtcNow));
});

app.MapDelete("/api/admin/keys/{code}", (HttpContext context, string code) =>
{
    var guard = RequireAdmin(context, writing: true);
    if (guard is not null)
    {
        return guard;
    }

    return store.DeleteKey(code)
        ? Results.Json(new { ok = true })
        : Results.Json(new { error = "not_found" }, statusCode: 404);
});

app.MapGet("/api/admin/keys.csv", (HttpContext context) =>
{
    var guard = RequireAdmin(context);
    if (guard is not null)
    {
        return guard;
    }

    var now = DateTimeOffset.UtcNow;
    var csv = new StringBuilder("code,state,service,days,daysLeft,activatedAt,expiresAt,device,customer,order\n");

    foreach (var key in store.AllKeys())
    {
        csv.Append($"{key.Code},{key.State(now)},{key.Service},{key.Days},{key.DaysLeft(now)},")
           .Append($"{key.ActivatedAt:yyyy-MM-dd},{key.ExpiresAt:yyyy-MM-dd},")
           .Append($"{Text.Csv(key.DeviceName)},{Text.Csv(key.Customer)},{Text.Csv(key.OrderCode)}\n");
    }

    return Results.Text(csv.ToString(), "text/csv");
});

// ------------------------------------------------------------ admin pricing

app.MapGet("/api/admin/pricing", (HttpContext context) =>
{
    var guard = RequireAdmin(context);
    if (guard is not null)
    {
        return guard;
    }

    var config = store.Config;
    var (rate, at, ready, stale) = pricing.CurrentRate();

    return Results.Json(new
    {
        config.Pricing.MarkupPercent,
        config.Pricing.RoundToToman,
        config.Pricing.RateMode,
        config.Pricing.ManualRateToman,
        config.Pricing.SourceUrl,
        config.Pricing.SourcePath,
        config.Pricing.SourceUnit,
        config.Pricing.RefreshMinutes,
        config.Pricing.MinRateToman,
        config.Pricing.MaxRateToman,
        config.Pricing.ShowUsd,
        config.Pricing.LastRateError,
        rate,
        rateAt = at,
        rateReady = ready,
        rateStale = stale,
        plans = config.Plans,
        preview = config.Plans.Select(p => new
        {
            p.Key,
            p.Label,
            p.UsdPrice,
            toman = PricingService.Quote(p.UsdPrice, rate, config.Pricing.MarkupPercent, config.Pricing.RoundToToman)
        })
    });
});

app.MapPost("/api/admin/pricing/test", async (HttpContext context, RateTestRequest request) =>
{
    var guard = RequireAdmin(context, writing: true);
    if (guard is not null)
    {
        return guard;
    }

    var result = await pricing.FetchAsync(
        request.Url ?? string.Empty,
        request.Path ?? string.Empty,
        request.Unit ?? "toman");

    return Results.Json(new { ok = result.Ok, toman = result.Toman, raw = result.Raw, error = result.Error });
});

app.MapPost("/api/admin/pricing/refresh", async (HttpContext context) =>
{
    var guard = RequireAdmin(context, writing: true);
    if (guard is not null)
    {
        return guard;
    }

    var result = await pricing.RefreshAsync(force: true);
    return Results.Json(new { ok = result.Ok, toman = result.Toman, error = result.Error });
});

// Housekeeping for the in-memory rate limit table.
var sweeper = new Timer(_ => limiter.Sweep(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
app.Lifetime.ApplicationStopping.Register(() => sweeper.Dispose());
app.Lifetime.ApplicationStopping.Register(() => pricing.Dispose());

app.Logger.LogInformation("Data folder: {Root}", store.Root);
app.Run(urls);
