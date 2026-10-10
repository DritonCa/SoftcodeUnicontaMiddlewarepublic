using SoftcodeUnicontaMiddleware.Services.Erp.Demo;
using Xunit;

namespace SoftcodeUnicontaMiddleware.Tests;

/// <summary>
/// The public demo must never mix testers' data and must clean up after itself.
/// </summary>
public class DemoErpStoreTests
{
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Every_demo_company_gets_the_fictional_catalogue()
    {
        var store = new DemoErpStore();

        Assert.Equal(8, store.With(1, c => c.Products.Count));
        Assert.Equal(3, store.With(1, c => c.Debtors.Count));
    }

    [Fact]
    public void Demo_companies_are_isolated()
    {
        var store = new DemoErpStore();
        store.With(1, c => c.NextInvoiceNumber = 99999);

        Assert.Equal(70001, store.With(2, c => c.NextInvoiceNumber));
        Assert.Equal(99999, store.With(1, c => c.NextInvoiceNumber));
    }

    [Fact]
    public void A_company_is_reseeded_after_its_lifetime()
    {
        var clock = new FakeClock();
        var store = new DemoErpStore(clock);
        store.With(1, c => { c.Products.Clear(); return 0; });

        clock.Now += DemoErpStore.Lifetime - TimeSpan.FromMinutes(1);
        Assert.Equal(0, store.With(1, c => c.Products.Count));

        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(8, store.With(1, c => c.Products.Count));
    }

    [Fact]
    public void Demo_invoice_is_a_pdf_with_its_number()
    {
        var pdf = DemoInvoicePdf.Render(70001, 5001, "Example (Æble) Shop",
            new[] { ("DW-1001", "Mug", 2.0, 39.96), ("", "Participant: Jane Doe", 0.0, 0.0), ("SHIP-1", "Shipping", 0.0, 49.0) },
            128.92);
        var text = System.Text.Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("DEMO INVOICE 70001", text);
        Assert.Contains("\\(", text); // parentheses are escaped
        Assert.EndsWith("%%EOF\n", text);
    }
}
