using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeWatch.Orders;

/// <summary>
/// Orders and settings on disk as plain JSON. No database to install, no
/// migrations, and a backup is a copy of one folder. Every write goes to a
/// temporary file first and is then moved into place, so a crash mid-write
/// cannot leave a half-written file behind.
/// </summary>
public sealed class Store
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // No 0/O/1/I: codes get read aloud and typed by hand.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _ordersFile;
    private readonly string _configFile;
    private readonly string _backupFolder;

    private List<Order> _orders = new();
    private ServiceConfig _config = ServiceConfig.CreateDefault();
    private DateOnly _lastBackup = DateOnly.MinValue;

    public Store(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);

        _backupFolder = Path.Combine(_root, "backups");
        Directory.CreateDirectory(_backupFolder);

        _ordersFile = Path.Combine(_root, "orders.json");
        _configFile = Path.Combine(_root, "config.json");

        Load();
    }

    public string Root => _root;

    public ServiceConfig Config
    {
        get { lock (_gate) { return _config; } }
    }

    // -------------------------------------------------------------- loading

    private void Load()
    {
        lock (_gate)
        {
            _orders = Read<List<Order>>(_ordersFile) ?? new List<Order>();
            _config = Read<ServiceConfig>(_configFile) ?? ServiceConfig.CreateDefault();

            if (_config.Plans.Count == 0)
            {
                _config.Plans = ServiceConfig.CreateDefault().Plans;
            }
        }
    }

    private static T? Read<T>(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return default;
            }

            var text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<T>(text, Json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[store] could not read {path}: {ex.Message}");
            return default;
        }
    }

    private static void WriteAtomic(string path, object value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));

        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    /// <summary>Keeps one snapshot per day, so a bad edit is always recoverable.</summary>
    private void BackupIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_lastBackup == today || !File.Exists(_ordersFile))
        {
            return;
        }

        try
        {
            File.Copy(_ordersFile, Path.Combine(_backupFolder, $"orders-{today:yyyy-MM-dd}.json"), true);
            _lastBackup = today;

            foreach (var stale in Directory.GetFiles(_backupFolder, "orders-*.json")
                         .OrderByDescending(f => f)
                         .Skip(60))
            {
                File.Delete(stale);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[store] backup failed: {ex.Message}");
        }
    }

    // --------------------------------------------------------------- orders

    public Order Create(Order order)
    {
        lock (_gate)
        {
            order.Id = Guid.NewGuid().ToString("N");
            order.Code = NextCode();
            order.CreatedAt = DateTimeOffset.UtcNow;
            order.UpdatedAt = order.CreatedAt;
            order.History.Add(new OrderEvent { Status = order.Status, Note = "Order received" });

            _orders.Add(order);
            Persist();
            return order;
        }
    }

    public Order? ByCode(string code)
    {
        lock (_gate)
        {
            return _orders.FirstOrDefault(o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase));
        }
    }

    public Order? ById(string id)
    {
        lock (_gate)
        {
            return _orders.FirstOrDefault(o => o.Id == id);
        }
    }

    public IReadOnlyList<Order> All()
    {
        lock (_gate)
        {
            return _orders.OrderByDescending(o => o.CreatedAt).ToList();
        }
    }

    public (IReadOnlyList<Order> Items, int Total) Search(string? status, string? query, int skip, int take)
    {
        lock (_gate)
        {
            IEnumerable<Order> result = _orders;

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<OrderStatus>(status, true, out var parsed))
            {
                result = result.Where(o => o.Status == parsed);
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                var needle = query.Trim();
                result = result.Where(o =>
                    o.Code.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    o.AccountEmail.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    o.Contact.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    o.PlanLabel.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    o.OwnerNote.Contains(needle, StringComparison.OrdinalIgnoreCase));
            }

            var ordered = result.OrderByDescending(o => o.CreatedAt).ToList();
            return (ordered.Skip(skip).Take(take).ToList(), ordered.Count);
        }
    }

    public Order? Update(string id, OrderUpdate change)
    {
        lock (_gate)
        {
            var order = _orders.FirstOrDefault(o => o.Id == id);
            if (order is null)
            {
                return null;
            }

            var statusChanged = false;

            if (!string.IsNullOrWhiteSpace(change.Status) &&
                Enum.TryParse<OrderStatus>(change.Status, true, out var status) &&
                status != order.Status)
            {
                order.Status = status;
                statusChanged = true;
            }

            if (change.OwnerNote is not null) order.OwnerNote = Text.Clip(change.OwnerNote, 2000);
            if (change.ChargedAmount is not null) order.ChargedAmount = change.ChargedAmount;
            if (change.ChargedCurrency is not null) order.ChargedCurrency = Text.Clip(change.ChargedCurrency, 12);
            if (change.CostAmount is not null) order.CostAmount = change.CostAmount;
            if (change.CostCurrency is not null) order.CostCurrency = Text.Clip(change.CostCurrency, 12);
            if (change.PaymentRef is not null) order.PaymentRef = Text.Clip(change.PaymentRef, 200);
            if (change.RenewsAt is not null) order.RenewsAt = change.RenewsAt;

            order.UpdatedAt = DateTimeOffset.UtcNow;

            if (statusChanged || !string.IsNullOrWhiteSpace(change.HistoryNote))
            {
                order.History.Add(new OrderEvent
                {
                    Status = order.Status,
                    Note = Text.Clip(change.HistoryNote ?? string.Empty, 500)
                });
            }

            Persist();
            return order;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var removed = _orders.RemoveAll(o => o.Id == id) > 0;
            if (removed)
            {
                Persist();
            }

            return removed;
        }
    }

    // --------------------------------------------------------------- config

    public void SaveConfig(Action<ServiceConfig> edit)
    {
        lock (_gate)
        {
            edit(_config);
            WriteAtomic(_configFile, _config);
        }
    }

    private void Persist()
    {
        BackupIfNeeded();
        WriteAtomic(_ordersFile, _orders);
    }

    private string NextCode()
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var builder = new StringBuilder("CW-");
            for (var i = 0; i < 6; i++)
            {
                builder.Append(CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]);
            }

            var candidate = builder.ToString();
            if (_orders.All(o => !string.Equals(o.Code, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return "CW-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    }

    // ---------------------------------------------------------------- stats

    public object Summary()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var thisMonth = _orders.Where(o => o.CreatedAt >= monthStart).ToList();

            return new
            {
                total = _orders.Count,
                open = _orders.Count(o => o.Status is OrderStatus.New or OrderStatus.Confirmed or OrderStatus.AwaitingPayment),
                byStatus = Enum.GetValues<OrderStatus>()
                    .ToDictionary(s => s.ToString(), s => _orders.Count(o => o.Status == s)),
                monthCount = thisMonth.Count,
                monthCost = thisMonth.Where(o => o.CostAmount.HasValue).Sum(o => o.CostAmount!.Value),
                monthCharged = thisMonth.Where(o => o.ChargedAmount.HasValue).Sum(o => o.ChargedAmount!.Value)
            };
        }
    }

    public string ExportCsv()
    {
        lock (_gate)
        {
            var builder = new StringBuilder();
            builder.AppendLine("code,created,updated,status,plan,months,email,contact,contact_kind,country,charged,charged_currency,cost,cost_currency,payment_ref,renews_at,note,owner_note");

            foreach (var order in _orders.OrderBy(o => o.CreatedAt))
            {
                builder.AppendLine(string.Join(',', new[]
                {
                    Text.Csv(order.Code),
                    Text.Csv(order.CreatedAt.ToString("u")),
                    Text.Csv(order.UpdatedAt.ToString("u")),
                    Text.Csv(order.Status.ToString()),
                    Text.Csv(order.PlanLabel),
                    Text.Csv(order.Months.ToString()),
                    Text.Csv(order.AccountEmail),
                    Text.Csv(order.Contact),
                    Text.Csv(order.ContactKind),
                    Text.Csv(order.Country),
                    Text.Csv(order.ChargedAmount?.ToString() ?? string.Empty),
                    Text.Csv(order.ChargedCurrency),
                    Text.Csv(order.CostAmount?.ToString() ?? string.Empty),
                    Text.Csv(order.CostCurrency),
                    Text.Csv(order.PaymentRef),
                    Text.Csv(order.RenewsAt?.ToString("u") ?? string.Empty),
                    Text.Csv(order.Note),
                    Text.Csv(order.OwnerNote)
                }));
            }

            return builder.ToString();
        }
    }
}

public static class Text
{
    public static string Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    /// <summary>Quotes a CSV field, and defuses the leading characters spreadsheets treat as formulas.</summary>
    public static string Csv(string? value)
    {
        var text = value ?? string.Empty;

        if (text.Length > 0 && (text[0] is '=' or '+' or '-' or '@'))
        {
            text = "'" + text;
        }

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
