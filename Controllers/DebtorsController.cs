using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftcodeUnicontaMiddleware.Models.Dtos;
using SoftcodeUnicontaMiddleware.Services.Erp;

namespace SoftcodeUnicontaMiddleware.Controllers;

/// <summary>Debtors (customers) from the ERP.</summary>
[Authorize]
[ApiController]
[Route("api/uniconta/debtors")]
[Produces("application/json")]
public class DebtorsController : ControllerBase
{
    private readonly IErpGateway _erp;

    public DebtorsController(IErpGateway erp) => _erp = erp;

    /// <summary>List debtors, paged.</summary>
    /// <param name="offset">Zero-based offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="includeDynamic">Add extra flat ERP fields under "extensions".</param>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Get(int offset = 0, int limit = 100, bool includeDynamic = false) =>
        Ok(await _erp.GetDebtorsAsync(offset, limit, includeDynamic));

    /// <summary>A single debtor by account number.</summary>
    [HttpGet("{account}")]
    [ProducesResponseType(typeof(DebtorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOne(string account, bool includeDynamic = false)
    {
        var debtor = await _erp.GetDebtorAsync(account, includeDynamic);
        return debtor == null ? NotFound() : Ok(debtor);
    }
}
