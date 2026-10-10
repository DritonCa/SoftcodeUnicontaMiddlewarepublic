using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftcodeUnicontaMiddleware.Models.Common;
using SoftcodeUnicontaMiddleware.Models.Dtos;
using SoftcodeUnicontaMiddleware.Services.Erp;

namespace SoftcodeUnicontaMiddleware.Controllers;

/// <summary>Products (items) from the ERP.</summary>
[Authorize]
[ApiController]
[Route("api/uniconta/products")]
[Produces("application/json")]
public class ProductsController : ControllerBase
{
    private readonly IErpGateway _erp;

    public ProductsController(IErpGateway erp) => _erp = erp;

    /// <summary>List products, paged.</summary>
    /// <param name="offset">Zero-based offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="includeDynamic">Add extra flat ERP fields under "extensions".</param>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<ProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Get(int offset = 0, int limit = 100, bool includeDynamic = false) =>
        Ok(await _erp.GetProductsAsync(offset, limit, includeDynamic));

    /// <summary>A single product by item number (SKU).</summary>
    [HttpGet("{sku}")]
    [ProducesResponseType(typeof(ProductDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOne(string sku, bool includeDynamic = false)
    {
        var product = await _erp.GetProductAsync(sku, includeDynamic);
        return product == null ? NotFound() : Ok(product);
    }
}
