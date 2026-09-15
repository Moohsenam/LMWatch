using System.Text;
using ClaudeWatch.Orders;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

var dataRoot = Environment.GetEnvironmentVariable("CW_DATA")
               ?? Path.Combine(AppContext.BaseDirectory, "data");
// One place decides the port. CW_URLS still wins when it is set, so an install
// that already pins a full address keeps working untouched.
var port = Environment.GetEnvironmentVariable("CW_PORT") is { Length: > 0 } p
           && int.TryParse(p, out var parsed) && parsed is > 0 and < 65536
    ? parsed
    : 5080;

var urls = Environment.GetEnvironmentVariable("CW_URLS") is { Length: > 0 } bind
    ? bind
    : $"http://127.0.0.1:{port}";

var store = new Store(dataRoot);
var auth = new Auth(store);
var limiter = new RateLimiter();
var pricing = new PricingService(store);
var releases = new ReleaseStore(dataRoot);
var mirror = new GitHubMirror(store, releases);

builder.Services.AddSingleton(store);
builder.Services.AddSingleton(auth);
builder.Services.AddSingleton(limiter);
builder.Services.AddSingleton(pricing);
builder.Services.AddSingleton(releases);
builder.Services.AddSingleton(mirror);

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
        $"SafeChat orders — admin password{Environment.NewLine}{generated}{Environment.NewLine}" +
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

/// <summary>
/// The address the caller reached us on, so a download link works whether the
/// request came through Caddy, nginx or straight to the port. Only ever handed
/// back to the same caller, so a forged Host header can mislead nobody else.
/// </summary>
string Root(HttpContext context)
{
    var scheme = First(context.Request.Headers["X-Forwarded-Proto"].ToString());
    if (scheme.Length == 0)
    {
        scheme = context.Request.Scheme;
    }

    var host = First(context.Request.Headers["X-Forwarded-Host"].ToString());
    if (host.Length == 0)
    {
        host = context.Request.Host.Value ?? string.Empty;
    }

    return $"{scheme}://{host}";

    static string First(string value) => value.Split(',')[0].Trim();
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
            .Select(p => new { p.Key, p.Label, p.LabelFa, p.PriceHint, p.Period, p.Service })
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

// The keys already issued against this order, so the panel can say "this one
// has had its key" instead of leaving it to memory.
app.MapGet("/api/admin/orders/{id}/keys", (HttpContext context, string id) =>
{
    var guard = RequireAdmin(context);
    if (guard is not null)
    {
        return guard;
    }

    var order = store.ById(id);

    if (order is null)
    {
        return Results.NotFound();
    }

    var now = DateTimeOffset.UtcNow;
    return Results.Json(new { items = store.KeysForOrder(order.Code).Select(k => k.ToAdmin(now)) });
});

// Issuing a key for an order, in one press. Everything the key needs is
// already on the order, so nothing is retyped and nothing gets mistyped.
app.MapPost("/api/admin/orders/{id}/key", (HttpContext context, string id, OrderKeyRequest? request) =>
{
    var guard = RequireAdmin(context, writing: true);
    if (guard is not null)
    {
        return guard;
    }

    var order = store.ById(id);

    if (order is null)
    {
        return Results.NotFound();
    }

    // A month is charged as 30 days. The owner can stretch it in the keys tab
    // if a customer needs the benefit of the doubt.
    var days = request?.Days is > 0 ? request.Days : Math.Max(1, order.Months) * 30;

    var service = order.Service?.ToLowerInvariant() switch
    {
        "chatgpt" => "chatgpt",
        "claude" => "claude",
        _ => "both"
    };

    var made = store.MakeKeys(
        1,
        Math.Clamp(days, 1, 3650),
        service,
        Text.Clip(string.IsNullOrWhiteSpace(order.FullName) ? order.AccountEmail : order.FullName, 120),
        Text.Clip(order.PlanLabel, 300),
        order.Code);

    app.Logger.LogInformation("Issued key for order {Code}", order.Code);

    return Results.Json(new { ok = true, code = made[0].Code, days });
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
                    Popular = p.Popular,
                    Service = p.Service?.Trim().ToLowerInvariant() == "chatgpt" ? "chatgpt" : "claude"
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

app.MapGet("/api/admin/stats", (HttpContext context, int? days) =>
{
    var guard = RequireAdmin(context);
    return guard ?? Results.Json(store.Stats(days ?? 30));
});

// A customer checking their own key. Deliberately thin: how long is left and
// nothing else, no device, no customer name, no note.
app.MapGet("/api/keys/{code}", (HttpContext context, string code) =>
{
    if (!limiter.Allow("keylookup:" + ClientKey(context), 10, TimeSpan.FromMinutes(10)))
    {
        return Results.Json(new { error = "too_many" }, statusCode: 429);
    }

    var key = store.KeyByCode(code);

    if (key is null)
    {
        return Results.Json(new { error = "not_found" }, statusCode: 404);
    }

    var now = DateTimeOffset.UtcNow;

    return Results.Json(new
    {
        state = key.State(now).ToString(),
        daysLeft = key.State(now) == KeyState.Active ? key.DaysLeft(now) : 0,
        expiresAt = key.ExpiresAt,
        service = key.Service
    });
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
            p.Service,
            p.Enabled,
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

// ------------------------------------------------------------- app releases

// What the desktop app asks every few hours. Deliberately public and cheap:
// version, what changed, where to get it, and the hash to check it against.
app.MapGet("/api/app/latest", (HttpContext context) =>
{
    if (!limiter.Allow("latest:" + ClientKey(context), 60, TimeSpan.FromMinutes(10)))
    {
        return Results.Json(new { error = "too_many" }, statusCode: 429);
    }

    var release = releases.Latest();

    if (release is null)
    {
        return Results.Json(new { version = "", url = "" });
    }

    return Results.Json(new
    {
        version = release.Version,
        notes = release.Notes,
        url = $"{Root(context)}/download/{release.Version}",
        sha256 = release.Sha256,
        size = release.Size,
        published = release.Published
    });
});

// The link on the site and in the app. /download is whatever is current.
app.MapGet("/download", (HttpContext context) =>
{
    var release = releases.Latest();

    return release is null
        ? Results.Json(new { error = "not_found" }, statusCode: 404)
        : Results.Redirect($"/download/{release.Version}");
});

app.MapGet("/download/{version}", (HttpContext context, string version) =>
{
    var release = releases.ByVersion(version);

    if (release is null || !release.Live)
    {
        return Results.Json(new { error = "not_found" }, statusCode: 404);
    }

    var path = releases.PathOf(release);

    if (!File.Exists(path))
    {
        return Results.Json(new { error = "not_found" }, statusCode: 404);
    }

    releases.CountDownload(release.Version);

    return Results.File(path, "application/octet-stream", release.FileName, enableRangeProcessing: true);
});

app.MapGet("/api/admin/releases", (HttpContext context) =>
{
    var guard = RequireAdmin(context);
    if (guard is not null)
    {
        return guard;
    }

    var config = store.Config;

    return Results.Json(new
    {
        items = releases.All(),
        latest = releases.Latest()?.Version ?? "",
        github = new
        {
            on = config.GitHubMirror,
            repo = config.GitHubRepo,
            // Enough to see a token is set, never enough to use it.
            token = config.GitHubToken.Length > 0 ? "••••••••" : "",
            lastTag = config.GitHubLastTag,
            lastCheck = mirror.LastCheck,
            lastCode = mirror.LastCode,
            lastResult = mirror.LastResult
        }
    });
});

// Where builds come from. The token is write-only from here: it goes in, it is
// never read back out, and leaving the field alone keeps the one already set.
app.MapPost("/api/admin/releases/github", (HttpContext context, GitHubSettings request) =>
{
    var guard = RequireAdmin(context, writing: true);
    if (guard is not null)
    {
        return guard;
    }

    store.SaveConfig(config =>
    {
        config.GitHubMirror = request.On;
        config.GitHubRepo = (request.Repo ?? string.Empty).Trim().Trim('/');

        if (!string.IsNullOrWhiteSpace(request.Token) && !request.Token.StartsWith("••"))
        {
            config.GitHubToken = request.Token.Trim();
        }

        if (request.Forget)
        {
            config.GitHubToken = string.Empty;
        }
    });

    return Results.Json(new { ok = true });
});

app.MapPost("/api/admin/releases/github/pull", async (HttpContext context, bool? force) =>
{
    var guard = RequireAdmin(context, writing: true);
    if (guard is not null)
    {
        return guard;
    }

    var result = await mirror.PullAsync(force ?? false);
    return Results.Json(new { ok = true, code = mirror.LastCode, result });
});

// The installer itself, as the raw body. A build is tens of megabytes, so it
// goes straight to a file rather than through memory, and the body limit is
// lifted for this one route rather than for the whole server.
app.MapPut("/api/admin/releases/{version}", async (HttpContext context, string version, string? notes) =>
{
    var guard = RequireAdmin(context, writing: true);
    if (guard is not null)
    {
        return guard;
    }

    string clean;
    try
    {
        clean = ReleaseStore.CleanVersion(version);
    }
    catch (ArgumentException ex)
    {
        return Results.Json(new { error = "bad_version", detail = ex.Message }, statusCode: 400);
    }

    var sizeLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (sizeLimit is { IsReadOnly: false })
    {
        sizeLimit.MaxRequestBodySize = 400L * 1024 * 1024;
    }

    var temp = Path.Combine(releases.Folder, $"upload-{Guid.NewGuid():N}.tmp");

    try
    {
        await using (var file = File.Create(temp))
        {
            await context.Request.Body.CopyToAsync(file);
        }

        if (new FileInfo(temp).Length < 1024)
        {
            File.Delete(temp);
            return Results.Json(new { error = "empty" }, statusCode: 400);
        }

        var published = releases.Publish(clean, notes ?? string.Empty, temp);

        app.Logger.LogInformation("Release {Version} published, {Size} bytes",
            published.Version, published.Size);

        return Results.Json(new
        {
            ok = true,
            version = published.Version,
            sha256 = published.Sha256,
            size = published.Size,
            url = $"{Root(context)}/download/{published.Version}"
        });
    }
    catch (Exception ex)
    {
        if (File.Exists(temp))
        {
            try { File.Delete(temp); } catch { /* the temp file is not worth a second failure */ }
        }

        app.Logger.LogError(ex, "Release upload failed");
        return Results.Json(new { error = "upload_failed" }, statusCode: 500);
    }
});

app.MapPost("/api/admin/releases/{version}/live", (HttpContext context, string version, bool? on) =>
{
    var guard = RequireAdmin(context, writing: true);
    if (guard is not null)
    {
        return guard;
    }

    var release = releases.SetLive(version, on ?? true);

    return release is null
        ? Results.Json(new { error = "not_found" }, statusCode: 404)
        : Results.Json(new { ok = true, version = release.Version, live = release.Live });
});

app.MapDelete("/api/admin/releases/{version}", (HttpContext context, string version) =>
{
    var guard = RequireAdmin(context, writing: true);
    if (guard is not null)
    {
        return guard;
    }

    return releases.Delete(version)
        ? Results.Json(new { ok = true })
        : Results.Json(new { error = "not_found" }, statusCode: 404);
});

// Housekeeping for the in-memory rate limit table.
var sweeper = new Timer(_ => limiter.Sweep(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
app.Lifetime.ApplicationStopping.Register(() => sweeper.Dispose());
app.Lifetime.ApplicationStopping.Register(() => pricing.Dispose());

mirror.Start();
app.Lifetime.ApplicationStopping.Register(() => mirror.Dispose());

app.Logger.LogInformation("Data folder: {Root}", store.Root);
app.Run(urls);
