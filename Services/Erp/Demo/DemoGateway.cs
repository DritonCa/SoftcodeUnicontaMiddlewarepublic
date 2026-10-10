using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using SoftcodeUnicontaMiddleware.Models.Common;
using SoftcodeUnicontaMiddleware.Models.Dtos;
using SoftcodeUnicontaMiddleware.Models.Orders;

namespace SoftcodeUnicontaMiddleware.Services.Erp.Demo;

/// <summary>
/// The demo ERP: same endpoints and validation as the real one, fictional data.
///
/// Orders are matched to debtors on EAN, CVR and e-mail (like the real matching), a new
/// debtor is created otherwise, and invoicing produces an invoice number and a small PDF.
/// Nothing ever reaches a real Uniconta company.
/// </summary>
public class DemoGateway : IErpGateway
{
    private readonly DemoErpStore _store;
    private readonly IHttpContextAccessor _http;
    private readonly OrderOptions _options;

    public DemoGateway(DemoErpStore store, IHttpContextAccessor http, IOptions<OrderOptions> options)
    {
        _store = store;
        _http = http;
        _options = options.Value;
    }

    private int CompanyId =>
        int.TryParse(_http.HttpContext?.User.FindFirst("companyId")?.Value, out var id)
            ? id
            : throw new UnauthorizedAccessException("Demo session expired – please log in again");

    private T Use<T>(Func<DemoCompany, T> action) => _store.With(CompanyId, action);

    public Task<PagedResponse<ProductDto>> GetProductsAsync(int offset, int limit, bool includeDynamic) =>
        Task.FromResult(Use(c => Page(c.Products, offset, limit)));

    public Task<ProductDto?> GetProductAsync(string sku, bool includeDynamic) =>
        Task.FromResult(Use(c => c.Products.FirstOrDefault(p => string.Equals(p.Sku, sku, StringComparison.OrdinalIgnoreCase))));

    public Task<PagedResponse<object>> GetDebtorsAsync(int offset, int limit, bool includeDynamic) =>
        Task.FromResult(Use(c => Page<object>(c.Debtors, offset, limit)));

    public Task<DebtorDto?> GetDebtorAsync(string account, bool includeDynamic) =>
        Task.FromResult(Use(c => c.Debtors.FirstOrDefault(d => d.Account == account)));

    public Task<OrderResult> SubmitOrderAsync(OrderRequest req) => Task.FromResult(Use(c =>
    {
        if (c.Orders.ContainsKey(req.OrderId))
            return new OrderResult { Success = false, Message = $"Order {req.OrderId} already exists" };

        foreach (var item in req.Items)
            if (!c.Products.Any(p => string.Equals(p.Sku, item.Sku, StringComparison.OrdinalIgnoreCase)))
                return new OrderResult { Success = false, Message = $"Unknown item {item.Sku} (CreateOrderLine returned NoSuchItem)" };

        var (account, status) = MatchOrCreateDebtor(c, req);
        var order = new DemoOrder
        {
            Request = req,
            Account = account,
            Created = DateTime.UtcNow,
            Requisition = string.Format(CultureInfo.InvariantCulture, _options.RequisitionFormat, req.OrderId),
            OurRef = req.OurRef,
        };

        foreach (var item in req.Items)
        {
            var price = req.PricesIncludeTax ? item.Price * 0.8 : item.Price;
            order.Lines.Add((item.Sku, item.Name, item.Qty, price));
            foreach (var text in OrderService.BuildTextLines(req.OrderId, item))
                order.Lines.Add(("", text._Text, 0, 0));
        }
        if (!req.Items.All(i => i.IsNonShippable) && req.ShippingAmount > 0 && req.ShippingProductSku != "")
            order.Lines.Add((req.ShippingProductSku, "Shipping", 0, req.ShippingAmount));

        c.Orders[req.OrderId] = order;
        return new OrderResult { Success = true, Message = $"Order submitted to debtor {account} ({status})" };
    }));

    public Task<OrderSummary?> FindOrderAsync(int orderNumber) => Task.FromResult(Use(c =>
        c.Orders.TryGetValue(orderNumber, out var o)
            ? new OrderSummary(orderNumber, o.Account, o.Created, o.Requisition)
            : null));

    public Task<DeleteOrderOutcome> DeleteOrderAsync(int orderNumber) => Task.FromResult(Use(c =>
        c.Orders.Remove(orderNumber)
            ? new DeleteOrderOutcome(true, true, null)
            : new DeleteOrderOutcome(false, false, null)));

    public Task<InvoiceOutcome> InvoiceOrderAsync(int orderNumber, InvoiceOrderRequest request) => Task.FromResult(Use(c =>
    {
        if (!c.Orders.TryGetValue(orderNumber, out var order))
            return new InvoiceOutcome(InvoiceStatus.OrderNotFound, $"Order {orderNumber} not found in Uniconta (already invoiced?)");
        if (order.Lines.Count == 0)
            return new InvoiceOutcome(InvoiceStatus.NoLines, $"Order {orderNumber} has no lines to invoice");

        if (!string.IsNullOrWhiteSpace(request.OurRef))
            order.OurRef = request.OurRef;

        var number = c.NextInvoiceNumber++;
        var total = order.Lines.Sum(l => l.Qty > 0 ? l.Qty * l.Price : l.Price);
        var debtor = c.Debtors.FirstOrDefault(d => d.Account == order.Account);
        c.Invoices[number] = new DemoInvoice(number, orderNumber, order.Account, DateTime.UtcNow, total,
            DemoInvoicePdf.Render(number, orderNumber, debtor?.Name ?? order.Account, order.Lines, total));

        // Like Uniconta with DeleteOrder set: the invoiced sales order leaves the open orders.
        if (_options.DeleteOrderWhenInvoiced)
            c.Orders.Remove(orderNumber);

        return new InvoiceOutcome(InvoiceStatus.Posted, $"Invoice posted for order {orderNumber}", number);
    }));

    public Task<byte[]?> GetInvoicePdfAsync(int invoiceNumber) =>
        Task.FromResult(Use(c => c.Invoices.TryGetValue(invoiceNumber, out var i) ? i.Pdf : null));

    public Task<(byte[] Pdf, long InvoiceNumber)?> GetInvoicePdfByOrderAsync(int orderNumber) => Task.FromResult(Use(c =>
    {
        var invoice = c.Invoices.Values.Where(i => i.OrderNumber == orderNumber).MaxBy(i => i.Number);
        return invoice == null ? ((byte[], long)?)null : (invoice.Pdf, invoice.Number);
    }));

    private (string Account, string Status) MatchOrCreateDebtor(DemoCompany c, OrderRequest req)
    {
        static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

        var match =
            (req.CustomerType == "ean" && Digits(req.Ean) != ""
                ? c.Debtors.FirstOrDefault(d => d.Extensions.TryGetValue("EAN", out var e) && Digits(e?.ToString()) == Digits(req.Ean))
                : null)
            ?? (req.CustomerType == "cvr" && Digits(req.Cvr) != ""
                ? c.Debtors.FirstOrDefault(d => d.Extensions.TryGetValue("CVR", out var v) && Digits(v?.ToString()) == Digits(req.Cvr))
                : null)
            ?? c.Debtors.FirstOrDefault(d => string.Equals(d.Email, req.Email?.Trim(), StringComparison.OrdinalIgnoreCase));

        if (match != null)
            return (match.Account, "existing");

        var account = (c.NextDebtorAccount++).ToString(CultureInfo.InvariantCulture);
        var debtor = new DebtorDto
        {
            Account = account,
            Name = string.IsNullOrWhiteSpace(req.CompanyName) ? req.ContactName : req.CompanyName,
            Group = _options.GroupFor(req.CustomerType),
            Address1 = req.DeliveryAddress, Address2 = "", ZipCode = req.DeliveryPostcode, City = req.DeliveryCity,
            CountryCode = "DK", ContactPerson = req.ContactName, Email = req.Email, Phone = req.Phone ?? "", Mobile = "",
            PaymentTerms = req.PaymentCode, Extensions = new(),
        };
        if (!string.IsNullOrWhiteSpace(req.Ean)) debtor.Extensions["EAN"] = req.Ean;
        if (!string.IsNullOrWhiteSpace(req.Cvr)) debtor.Extensions["CVR"] = req.Cvr;
        c.Debtors.Add(debtor);
        return (account, "new");
    }

    private static PagedResponse<T> Page<T>(IReadOnlyList<T> all, int offset, int limit)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 500);
        return new PagedResponse<T> { Items = all.Skip(offset).Take(limit).ToList(), Offset = offset, Limit = limit, Total = all.Count };
    }
}

/// <summary>A minimal one-page PDF, enough to show that the invoice endpoints work.</summary>
internal static class DemoInvoicePdf
{
    public static byte[] Render(long number, int orderNumber, string customer,
                                IEnumerable<(string Sku, string Text, double Qty, double Price)> lines, double total)
    {
        var text = new List<string>
        {
            $"DEMO INVOICE {number}",
            $"Order {orderNumber} - {customer}",
            "Fictional data from the SoftCode Uniconta middleware demo.",
            "",
        };
        foreach (var l in lines)
            text.Add(l.Sku == "" ? $"    {l.Text}"
                : l.Qty == 0 ? $"{l.Sku}  {l.Text}  {l.Price.ToString("0.00", CultureInfo.InvariantCulture)}"
                : $"{l.Sku}  {l.Text}  {l.Qty} x {l.Price.ToString("0.00", CultureInfo.InvariantCulture)}");
        text.Add("");
        text.Add($"Total excl. VAT: {total.ToString("0.00", CultureInfo.InvariantCulture)}");

        var stream = new StringBuilder("BT /F1 11 Tf 50 790 Td 14 TL\n");
        foreach (var line in text)
            stream.Append('(').Append(Escape(line)).Append(") '\n");
        stream.Append("ET");
        var content = stream.ToString();

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream",
        };

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets)
            pdf.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static string Escape(string s) =>
        new string(s.Select(ch => ch > 255 ? '?' : ch).ToArray()).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
