using SoftcodeUnicontaMiddleware.Models.Dtos;
using SoftcodeUnicontaMiddleware.Models.Orders;

namespace SoftcodeUnicontaMiddleware.Services.Erp.Demo;

/// <summary>
/// In-memory, fictional ERP data for the public demo.
///
/// Every demo company (one per demo account) gets its own copy, so testers never see
/// each other's orders. A company is reseeded after <see cref="Lifetime"/>, which keeps
/// the demo small and resets anything a tester changed.
/// </summary>
public class DemoErpStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly Dictionary<int, DemoCompany> _companies = new();
    private readonly object _lock = new();
    private readonly TimeProvider _clock;

    public DemoErpStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Runs <paramref name="action"/> on the company under a lock.</summary>
    public T With<T>(int companyId, Func<DemoCompany, T> action)
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            if (!_companies.TryGetValue(companyId, out var company) || now - company.Created > Lifetime)
            {
                company = DemoCompany.Seed(now);
                _companies[companyId] = company;
            }
            return action(company);
        }
    }
}

public class DemoCompany
{
    public DateTimeOffset Created { get; private init; }
    public List<ProductDto> Products { get; } = new();
    public List<DebtorDto> Debtors { get; } = new();
    public Dictionary<int, DemoOrder> Orders { get; } = new();
    public Dictionary<long, DemoInvoice> Invoices { get; } = new();
    public long NextInvoiceNumber { get; set; } = 70001;
    public long NextDebtorAccount { get; set; } = 100004;

    public static DemoCompany Seed(DateTimeOffset now)
    {
        var c = new DemoCompany { Created = now };

        c.Products.AddRange(new[]
        {
            Product("DW-1001", "Demo coffee mug 30 cl, white", "KITCHEN", 39.96, 18.50, 342, 18),
            Product("DW-1002", "Demo thermos 1 l, black", "KITCHEN", 199.20, 92.00, 61, 4),
            Product("DW-1003", "Demo tea towels, 3-pack", "TEXTILE", 63.96, 21.00, 128, 0),
            Product("DW-2001", "Demo notebook A5, dotted", "OFFICE", 55.96, 19.90, 240, 12),
            Product("DW-2002", "Demo desk lamp, LED", "OFFICE", 319.20, 140.00, 17, 2),
            Product("DW-3001", "Demo gift card 500 kr", "GIFTCARD", 400.00, 0, 0, 0, isStockItem: false),
            Product("DW-9001", "Demo online course, 1 seat", "COURSE", 1196.00, 0, 0, 0, isStockItem: false),
            Product("SHIP-1", "Shipping", "SHIPPING", 0, 0, 0, 0, isStockItem: false),
        });

        c.Debtors.AddRange(new[]
        {
            Debtor("100001", "Demo Webshop ApS", "CVR", "Eksempelvej 1", "1000", "København K",
                   "Jane Doe", "orders@demo-webshop.example", "NK8", cvr: "12345678"),
            Debtor("100002", "Example Municipality, School Service", "EAN", "Rådhuspladsen 99", "2000", "Frederiksberg",
                   "John Smith", "invoices@municipality.example", "NK30", ean: "5790000000001"),
            Debtor("100003", "Anna Example", "PRIV", "Prøvegade 12", "8000", "Aarhus C",
                   "Anna Example", "anna@example.com", "NK0"),
        });

        return c;
    }

    private static ProductDto Product(string sku, string name, string group, double price, double cost,
                                      double onHand, double reserved, bool isStockItem = true) => new()
    {
        Sku = sku, Name = name, Group = group, SalesPrice = price, CostPrice = cost,
        StockOnHand = onHand, StockReserved = reserved, StockAvailable = onHand - reserved,
        IsStockItem = isStockItem, Unit = "pcs", Blocked = false, Extensions = new(),
    };

    private static DebtorDto Debtor(string account, string name, string group, string address, string zip, string city,
                                    string contact, string email, string terms, string? cvr = null, string? ean = null)
    {
        var debtor = new DebtorDto
        {
        Account = account, Name = name, Group = group, Address1 = address, Address2 = "", ZipCode = zip, City = city,
        CountryCode = "DK", ContactPerson = contact, Email = email, Phone = "", Mobile = "", PaymentTerms = terms,
        PaymentMethod = 0, CreditMax = 0, Balance = 0, Overdue = 0, Blocked = false, Extensions = new(),
        };
        if (cvr != null) debtor.Extensions["CVR"] = cvr;
        if (ean != null) debtor.Extensions["EAN"] = ean;
        return debtor;
    }
}

public class DemoOrder
{
    public required OrderRequest Request { get; init; }
    public required string Account { get; init; }
    public required DateTime Created { get; init; }
    public required string Requisition { get; init; }
    public string? OurRef { get; set; }
    public List<(string Sku, string Text, double Qty, double Price)> Lines { get; } = new();
}

public record DemoInvoice(long Number, int OrderNumber, string Account, DateTime Date, double Total, byte[] Pdf);
