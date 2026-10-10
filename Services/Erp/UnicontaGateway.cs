using SoftcodeUnicontaMiddleware.Models.Common;
using SoftcodeUnicontaMiddleware.Models.Dtos;
using SoftcodeUnicontaMiddleware.Models.Orders;
using SoftcodeUnicontaMiddleware.UnicontaService;
using Uniconta.Common;

namespace SoftcodeUnicontaMiddleware.Services.Erp;

/// <summary>
/// The real ERP: the Uniconta company of the logged-in caller.
///
/// A client is created per request from the credentials cached at login, so the
/// password never travels in the token. An expired session throws
/// <see cref="UnauthorizedAccessException"/>, which the exception middleware turns
/// into a 401.
/// </summary>
public class UnicontaGateway : IErpGateway
{
    private readonly UnicontaServiceClientFactory _factory;
    private readonly OrderService _orderService;
    private readonly IOrderLogger _orderLog;
    private readonly ILogger<UnicontaGateway> _logger;

    public UnicontaGateway(
        UnicontaServiceClientFactory factory,
        OrderService orderService,
        IOrderLogger orderLog,
        ILogger<UnicontaGateway> logger)
    {
        _factory = factory;
        _orderService = orderService;
        _orderLog = orderLog;
        _logger = logger;
    }

    public async Task<PagedResponse<ProductDto>> GetProductsAsync(int offset, int limit, bool includeDynamic) =>
        (await _factory.CreateAsync()).GetProductsPaged(offset, limit, includeDynamic);

    public async Task<ProductDto?> GetProductAsync(string sku, bool includeDynamic) =>
        (await _factory.CreateAsync()).GetProductBySku(sku, includeDynamic);

    public async Task<PagedResponse<object>> GetDebtorsAsync(int offset, int limit, bool includeDynamic) =>
        (await _factory.CreateAsync()).GetDebtorsPaged(offset, limit, includeDynamic);

    public async Task<DebtorDto?> GetDebtorAsync(string account, bool includeDynamic) =>
        (await _factory.CreateAsync()).GetDebtorByAccount(account, includeDynamic);

    public async Task<OrderResult> SubmitOrderAsync(OrderRequest request) =>
        await _orderService.ProcessAsync(request, await _factory.CreateAsync());

    public async Task<OrderSummary?> FindOrderAsync(int orderNumber)
    {
        var order = await (await _factory.CreateAsync()).GetOrderByNumberAsync(orderNumber);
        return order == null
            ? null
            : new OrderSummary(order._OrderNumber, order._DCAccount, order._Created, order.Requisition);
    }

    public async Task<DeleteOrderOutcome> DeleteOrderAsync(int orderNumber)
    {
        var (existed, deleted, error) = await (await _factory.CreateAsync()).DeleteOrderAsync(orderNumber);
        return new DeleteOrderOutcome(existed, deleted, error);
    }

    public async Task<InvoiceOutcome> InvoiceOrderAsync(int orderNumber, InvoiceOrderRequest request)
    {
        var client = await _factory.CreateAsync();

        var order = await client.GetOrderByNumberAsync(orderNumber);
        if (order == null)
        {
            _orderLog.LogFailed(orderNumber, "", "", "invoice: order not found in Uniconta (already invoiced?)");
            return new InvoiceOutcome(InvoiceStatus.OrderNotFound,
                $"Order {orderNumber} not found in Uniconta (already invoiced?)");
        }

        var headerChanged = false;
        if (!string.IsNullOrWhiteSpace(request.OurRef))
        {
            order._OurRef = request.OurRef;
            headerChanged = true;
        }

        // Orders created before the flag was set on creation would otherwise stay
        // behind as open sales orders next to their own invoice.
        if (!order._DeleteOrder)
        {
            order._DeleteOrder = true;
            headerChanged = true;
        }

        if (headerChanged)
        {
            var updateResult = await client.UpdateOrderHeaderAsync(order);
            if (updateResult != ErrorCodes.Succes)
                _logger.LogWarning("Could not update header on order {OrderNumber}: {Result}", orderNumber, updateResult);
        }

        var lines = await client.GetOrderLinesAsync(order);
        if (lines.Length == 0)
        {
            _orderLog.LogFailed(orderNumber, "", "", "invoice: order has no lines");
            return new InvoiceOutcome(InvoiceStatus.NoLines, $"Order {orderNumber} has no lines to invoice");
        }

        var result = await client.PostInvoiceAsync(order, lines);
        if (result == null || result.Err != ErrorCodes.Succes)
        {
            var msg = $"PostInvoice returned {result?.Err}";
            _orderLog.LogFailed(orderNumber, "", "", msg, detail: $"lines={lines.Length}");
            return new InvoiceOutcome(InvoiceStatus.Failed, msg);
        }

        // The number Uniconta assigned lives on the posted invoice header. It is optional
        // all the way through: odd postings can return no header, which is not a failure.
        long? invoiceNumber = result.Header?._InvoiceNumber;
        if (invoiceNumber is null or 0)
        {
            _logger.LogWarning("Invoice posted for order {OrderNumber} but Uniconta returned no invoice number", orderNumber);
            invoiceNumber = null;
        }

        _orderLog.LogSubmitted(
            orderNumber, "", "",
            $"invoice {invoiceNumber?.ToString() ?? "(no number)"}",
            request,
            new { invoiceNumber, lines = lines.Length, ourRef = request.OurRef });

        return new InvoiceOutcome(InvoiceStatus.Posted, $"Invoice posted for order {orderNumber}", invoiceNumber);
    }

    public async Task<byte[]?> GetInvoicePdfAsync(int invoiceNumber) =>
        await (await _factory.CreateAsync()).GetInvoicePdfAsync(invoiceNumber);

    public async Task<(byte[] Pdf, long InvoiceNumber)?> GetInvoicePdfByOrderAsync(int orderNumber)
    {
        var found = await (await _factory.CreateAsync()).GetInvoicePdfByOrderAsync(orderNumber);
        return found == null ? null : (found.Value.Pdf, found.Value.InvoiceNumber);
    }
}
