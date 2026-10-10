using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftcodeUnicontaMiddleware.Models.Orders;
using SoftcodeUnicontaMiddleware.Services;
using SoftcodeUnicontaMiddleware.Services.Erp;

namespace SoftcodeUnicontaMiddleware.Controllers;

/// <summary>Sales orders and invoices.</summary>
[Authorize]
[ApiController]
[Route("api/uniconta/orders")]
[Produces("application/json")]
public class OrdersController : ControllerBase
{
    private readonly IErpGateway _erp;
    private readonly ILogger<OrdersController> _logger;
    private readonly IOrderLogger _orderLog;

    public OrdersController(IErpGateway erp, ILogger<OrdersController> logger, IOrderLogger orderLog)
    {
        _erp = erp;
        _logger = logger;
        _orderLog = orderLog;
    }

    /// <summary>
    /// Create a sales order from a webshop order. The debtor is matched on EAN, CVR,
    /// e-mail or address, and created when none matches. Nothing is invoiced here.
    /// </summary>
    /// <response code="200">The sales order was created.</response>
    /// <response code="422">The ERP rejected the order.</response>
    [HttpPost]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Submit([FromBody] OrderRequest request)
    {
        // Logged on arrival, before validation, so every attempt can be followed.
        _logger.LogInformation(
            "Order received: orderId={OrderId} type={Type} items={Items} total={Total} shipping={Shipping}",
            request.OrderId, request.CustomerType, request.Items?.Count ?? 0, request.TotalPrice, request.ShippingAmount);
        _orderLog.LogReceived(request.OrderId, request.CustomerType ?? "", request.Email ?? "",
            $"items={request.Items?.Count ?? 0} total={request.TotalPrice} shipping={request.ShippingAmount}",
            request);

        if (request.OrderId <= 0)
            return BadRequest(new OrderResponse { Message = "Invalid OrderId" });
        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new OrderResponse { Message = "Email is required" });
        if (request.Items == null || request.Items.Count == 0)
            return BadRequest(new OrderResponse { Message = "At least one item is required" });

        var result = await _erp.SubmitOrderAsync(request);
        if (result.Success)
            return Ok(new OrderResponse { Accepted = true, Message = result.Message });

        _logger.LogWarning("Order {OrderId} was rejected: {Message}", request.OrderId, result.Message);
        return UnprocessableEntity(new OrderResponse { Accepted = false, Message = result.Message });
    }

    /// <summary>Does the sales order exist? Useful before cleaning up a cancelled order.</summary>
    [HttpGet("{orderNumber:int}")]
    [ProducesResponseType(typeof(OrderSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Find(int orderNumber)
    {
        if (orderNumber <= 0)
            return BadRequest(new OrderResponse { Message = "Invalid order number" });

        var order = await _erp.FindOrderAsync(orderNumber);
        return order == null
            ? NotFound(new OrderResponse { Message = $"No order {orderNumber} in Uniconta" })
            : Ok(order);
    }

    /// <summary>
    /// Delete the sales order, e.g. when the webshop cancels it. 404 means there was
    /// nothing to delete, so the call is safe to repeat.
    /// </summary>
    [HttpDelete("{orderNumber:int}")]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Delete(int orderNumber)
    {
        if (orderNumber <= 0)
            return BadRequest(new OrderResponse { Message = "Invalid order number" });

        var outcome = await _erp.DeleteOrderAsync(orderNumber);
        if (!outcome.Existed)
            return NotFound(new OrderResponse { Message = $"No order {orderNumber} in Uniconta" });
        if (!outcome.Deleted)
        {
            _logger.LogError("Deleting order {OrderNumber} failed: {Error}", orderNumber, outcome.Error);
            return UnprocessableEntity(new OrderResponse { Message = outcome.Error ?? "Delete failed" });
        }

        _logger.LogInformation("Order {OrderNumber} deleted", orderNumber);
        return Ok(new OrderResponse { Message = $"Order {orderNumber} deleted" });
    }

    /// <summary>
    /// Post the invoice for an existing sales order, e.g. when the payment is captured.
    /// Returns the invoice number the ERP assigned.
    /// </summary>
    /// <response code="404">The order does not exist (or was already invoiced).</response>
    [HttpPost("{orderNumber:int}/invoice")]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Invoice(int orderNumber, [FromBody] InvoiceOrderRequest request)
    {
        if (orderNumber <= 0)
            return BadRequest(new OrderResponse { Message = "Invalid order number" });

        var outcome = await _erp.InvoiceOrderAsync(orderNumber, request ?? new InvoiceOrderRequest());
        return outcome.Status switch
        {
            InvoiceStatus.Posted => Ok(new OrderResponse
            {
                Accepted = true, Message = outcome.Message, InvoiceNumber = outcome.InvoiceNumber
            }),
            InvoiceStatus.OrderNotFound => NotFound(new OrderResponse { Message = outcome.Message }),
            _ => UnprocessableEntity(new OrderResponse { Accepted = false, Message = outcome.Message }),
        };
    }

    /// <summary>The invoice PDF by invoice number.</summary>
    [HttpGet("invoice/{invoiceNumber:int}/pdf")]
    [Produces("application/pdf", "application/json")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> InvoicePdf(int invoiceNumber)
    {
        if (invoiceNumber <= 0)
            return BadRequest(new OrderResponse { Message = "Invalid invoice number" });

        var pdf = await _erp.GetInvoicePdfAsync(invoiceNumber);
        return pdf == null
            ? NotFound(new OrderResponse { Message = $"No PDF for invoice {invoiceNumber}" })
            : File(pdf, "application/pdf", $"Invoice-{invoiceNumber}.pdf");
    }

    /// <summary>
    /// The latest invoice PDF for a webshop order number. The invoice number is returned
    /// in the X-Uniconta-Invoice-Number header so the caller can store it.
    /// </summary>
    [HttpGet("{orderNumber:int}/invoice/pdf")]
    [Produces("application/pdf", "application/json")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> InvoicePdfByOrder(int orderNumber)
    {
        if (orderNumber <= 0)
            return BadRequest(new OrderResponse { Message = "Invalid order number" });

        var found = await _erp.GetInvoicePdfByOrderAsync(orderNumber);
        if (found == null)
            return NotFound(new OrderResponse { Message = $"No invoice for order {orderNumber}" });

        var (pdf, invoiceNumber) = found.Value;
        Response.Headers["X-Uniconta-Invoice-Number"] = invoiceNumber.ToString();
        return File(pdf, "application/pdf", $"Invoice-{invoiceNumber}.pdf");
    }
}
