using SoftcodeUnicontaMiddleware.Models.Orders;
using SoftcodeUnicontaMiddleware.Services;
using Uniconta.DataModel;
using Xunit;

namespace SoftcodeUnicontaMiddleware.Tests;

/// <summary>
/// Free-text lines under a product, e.g. who a course seat was bought for.
///
/// What can easily go wrong: the text landing in _Text on the product line itself
/// (replacing the item name on the invoice), an empty checkout field becoming a bare
/// gap on the invoice, or a text line moving stock.
/// </summary>
public class TextLineTests
{
    private static OrderItemRequest Item(params string[] texts) => new()
    {
        Sku = "COURSE-1",
        Name = "Example course",
        Price = 1000,
        Qty = 1,
        TextLines = new List<string>(texts)
    };

    [Fact]
    public void One_text_gives_one_text_line()
    {
        var lines = OrderService.BuildTextLines(1001, Item("Participant: Jane Doe"));

        Assert.Single(lines);
        Assert.Equal("Participant: Jane Doe", lines[0]._Text);
        Assert.Equal(1001, lines[0]._OrderNumber);
    }

    [Fact]
    public void Text_line_has_no_item_number_so_the_item_name_stays()
    {
        var lines = OrderService.BuildTextLines(1001, Item("Participant: Jane Doe"));

        Assert.True(string.IsNullOrEmpty(lines[0]._Item));
    }

    [Fact]
    public void Text_line_has_no_quantity_price_or_stock_movement()
    {
        var lines = OrderService.BuildTextLines(1001, Item("Participant: Jane Doe"));

        Assert.Equal(0, lines[0]._Qty);
        Assert.Equal(0, lines[0]._Price);
        Assert.Equal(StorageRegister.None, lines[0]._Storage);
    }

    [Fact]
    public void Product_line_itself_is_not_touched_by_the_text_lines()
    {
        var line = OrderService.BuildOrderLine(1001, Item("Participant: Jane Doe"), pricesIncludeTax: false);

        Assert.True(string.IsNullOrEmpty(line._Text));
        Assert.Equal("COURSE-1", line._Item);
    }

    [Fact]
    public void Several_lines_keep_their_order()
    {
        var lines = OrderService.BuildTextLines(1001, Item("A", "B", "C"));

        Assert.Equal(new[] { "A", "B", "C" }, lines.Select(l => l._Text));
    }

    [Fact]
    public void Blank_lines_are_dropped_and_text_is_trimmed()
    {
        var lines = OrderService.BuildTextLines(1001, Item("", "   ", "  John Smith  "));

        Assert.Single(lines);
        Assert.Equal("John Smith", lines[0]._Text);
    }

    [Fact]
    public void Duplicate_lines_appear_once()
    {
        Assert.Single(OrderService.BuildTextLines(1001, Item("John Smith", "john smith", "John Smith")));
    }

    [Fact]
    public void Missing_list_gives_no_text_lines()
    {
        var item = new OrderItemRequest { Sku = "1010", TextLines = null! };

        Assert.Empty(OrderService.BuildTextLines(1001, item));
    }
}
