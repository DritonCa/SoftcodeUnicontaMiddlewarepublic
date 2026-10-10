namespace SoftcodeUnicontaMiddleware.Models.Orders;

public class OrderResponse
{
    public bool Accepted { get; set; }
    public string Message { get; set; } = "";

    /// <summary>
    /// The invoice number Uniconta assigned when the sales order was posted.
    /// Only set by POST /orders/{n}/invoice, and only on success. The shop
    /// stores it on the order and prints it on the invoice, so the customer and
    /// the bookkeeping refer to the same number.
    /// Null on every other response — the caller must treat it as optional.
    /// </summary>
    public long? InvoiceNumber { get; set; }
}
