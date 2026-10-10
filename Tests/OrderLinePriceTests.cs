using SoftcodeUnicontaMiddleware.Models.Orders;
using SoftcodeUnicontaMiddleware.Services;
using Xunit;

namespace SoftcodeUnicontaMiddleware.Tests;

/// <summary>
/// The price on an order line. Uniconta wants the amount WITHOUT VAT and adds VAT
/// itself from the item's own setup, so the price must never be derived from the
/// shape of the item number.
/// </summary>
public class OrderLinePriceTests
{
    private static OrderItemRequest Item(string sku, double price, int qty = 1) =>
        new() { Sku = sku, Price = price, Qty = qty };

    [Fact]
    public void Price_excluding_vat_passes_through_unchanged()
    {
        var line = OrderService.BuildOrderLine(1001, Item("COURSE-1", 25750), pricesIncludeTax: false);

        Assert.Equal(25750, line._Price);
        Assert.Equal("COURSE-1", line._Item);
        Assert.Equal(1001, line._OrderNumber);
    }

    [Fact]
    public void Item_number_length_does_not_change_the_price()
    {
        var four = OrderService.BuildOrderLine(1, Item("7000", 1000), pricesIncludeTax: false);
        var three = OrderService.BuildOrderLine(1, Item("750", 1000), pricesIncludeTax: false);

        Assert.Equal(three._Price, four._Price);
        Assert.Equal(1000, four._Price);
    }

    [Fact]
    public void Prices_including_vat_have_25_percent_vat_stripped()
    {
        var line = OrderService.BuildOrderLine(1, Item("BOOK-1", 325), pricesIncludeTax: true);

        Assert.Equal(260, line._Price);
    }

    [Fact]
    public void Prices_exclude_vat_by_default()
    {
        Assert.False(new OrderRequest().PricesIncludeTax);
    }

    [Theory]
    [InlineData(25750)]
    [InlineData(325)]
    [InlineData(0.5)]
    public void Quantity_and_amount_are_carried_over(double price)
    {
        var line = OrderService.BuildOrderLine(99, Item("1100", price, qty: 3), pricesIncludeTax: false);

        Assert.Equal(price, line._Price);
        Assert.Equal(3, line._Qty);
    }
}
