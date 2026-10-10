using SoftcodeUnicontaMiddleware.Models.Orders;
using SoftcodeUnicontaMiddleware.Services;
using Uniconta.Common;
using Xunit;

namespace SoftcodeUnicontaMiddleware.Tests;

/// <summary>
/// The sales order header and the configurable parts of the mapping.
///
/// The DeleteOrder flag is tested explicitly because nothing in the middleware reads
/// it: a mistake would only show weeks later as a growing pile of open sales orders
/// in Uniconta.
/// </summary>
public class OrderHeaderTests
{
    private static OrderRequest Order() => new()
    {
        OrderId = 1001,
        CustomerType = "cvr",
        Email = "orders@example.com",
        Cvr = "12345678",
        CompanyName = "Example Shop ApS",
        ContactName = "Jane Doe",
        TotalPrice = 1250
    };

    [Fact]
    public void Invoiced_sales_order_is_removed_by_default()
    {
        Assert.True(OrderService.BuildOrderHeader(Order(), "100001", new OrderOptions())._DeleteOrder);
    }

    [Fact]
    public void Removal_after_invoicing_can_be_switched_off()
    {
        var options = new OrderOptions { DeleteOrderWhenInvoiced = false };
        Assert.False(OrderService.BuildOrderHeader(Order(), "100001", options)._DeleteOrder);
    }

    [Fact]
    public void Header_carries_account_order_number_and_requisition()
    {
        var header = OrderService.BuildOrderHeader(Order(), "100001", new OrderOptions());

        Assert.Equal("100001", header._DCAccount);
        Assert.Equal(1001, header._OrderNumber);
        Assert.Equal("Web order 1001", header._Requisition);
    }

    [Fact]
    public void Requisition_format_is_configurable()
    {
        var options = new OrderOptions { RequisitionFormat = "Webordre: 0{0}" };
        Assert.Equal("Webordre: 01001", OrderService.BuildOrderHeader(Order(), "100001", options)._Requisition);
    }

    [Fact]
    public void Debtor_group_vat_code_and_country_come_from_configuration()
    {
        var options = new OrderOptions
        {
            DebtorVatCode = "S25",
            Country = "Sweden",
            DebtorGroups = new Dictionary<string, string> { ["cvr"] = "B2B", ["privat"] = "B2C" }
        };

        var debtor = OrderService.BuildDebtor(Order(), "100001", options);

        Assert.Equal("B2B", debtor._Group);
        Assert.Equal("S25", debtor._Vat);
        Assert.Equal(CountryCode.Sweden, debtor._Country);
        Assert.Equal("12345678", debtor._VatNumber);
    }

    [Fact]
    public void Unknown_customer_type_falls_back_to_the_private_group()
    {
        var order = Order();
        order.CustomerType = "reseller";

        Assert.Equal("PRIV", OrderService.BuildDebtor(order, "100001", new OrderOptions())._Group);
    }

    [Fact]
    public void Ean_customer_gets_gln_and_electronic_invoicing()
    {
        var order = Order();
        order.CustomerType = "ean";
        order.Ean = "5790000000001";

        var debtor = OrderService.BuildDebtor(order, "100001", new OrderOptions());

        Assert.Equal("EAN", debtor._Group);
        Assert.Equal("5790000000001", debtor._EAN);
        Assert.True(debtor._InvoiceInXML);
    }
}
