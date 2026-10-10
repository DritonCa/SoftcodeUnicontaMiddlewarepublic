using SoftcodeUnicontaMiddleware.Models.Common;
using SoftcodeUnicontaMiddleware.Models.Dtos;
using SoftcodeUnicontaMiddleware.Models.Orders;

namespace SoftcodeUnicontaMiddleware.Services.Erp;

/// <summary>
/// Everything the API controllers need from the ERP, in one place.
///
/// <see cref="UnicontaGateway"/> talks to a real Uniconta company; <see cref="Demo.DemoGateway"/>
/// serves fictional data for the public demo. Which one is used is decided by the
/// "Erp:DataSource" setting, so the API, its validation and its error codes are the
/// same in both.
/// </summary>
public interface IErpGateway
{
    Task<PagedResponse<ProductDto>> GetProductsAsync(int offset, int limit, bool includeDynamic);
    Task<ProductDto?> GetProductAsync(string sku, bool includeDynamic);

    Task<PagedResponse<object>> GetDebtorsAsync(int offset, int limit, bool includeDynamic);
    Task<DebtorDto?> GetDebtorAsync(string account, bool includeDynamic);

    Task<OrderResult> SubmitOrderAsync(OrderRequest request);
    Task<OrderSummary?> FindOrderAsync(int orderNumber);
    Task<DeleteOrderOutcome> DeleteOrderAsync(int orderNumber);
    Task<InvoiceOutcome> InvoiceOrderAsync(int orderNumber, InvoiceOrderRequest request);

    Task<byte[]?> GetInvoicePdfAsync(int invoiceNumber);
    Task<(byte[] Pdf, long InvoiceNumber)?> GetInvoicePdfByOrderAsync(int orderNumber);
}

public record OrderSummary(int OrderNumber, string Account, DateTime Created, string? Requisition);

public record DeleteOrderOutcome(bool Existed, bool Deleted, string? Error);

public enum InvoiceStatus { Posted, OrderNotFound, NoLines, Failed }

public record InvoiceOutcome(InvoiceStatus Status, string Message, long? InvoiceNumber = null);

/// <summary>"Erp" configuration section.</summary>
public class ErpOptions
{
    public const string Section = "Erp";

    /// <summary>"Uniconta" (default) or "Demo".</summary>
    public string DataSource { get; set; } = "Uniconta";

    public bool IsDemo => string.Equals(DataSource, "Demo", StringComparison.OrdinalIgnoreCase);
}
