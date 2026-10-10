using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SoftcodeUnicontaMiddleware.Data;
using SoftcodeUnicontaMiddleware.Data.Entities;
using SoftcodeUnicontaMiddleware.Services;
using SoftcodeUnicontaMiddleware.Services.Erp;

namespace SoftcodeUnicontaMiddleware.Controllers;

/// <summary>
/// Self-service demo accounts. Only available when Erp:DataSource is "Demo", so a
/// production install never hands out credentials to strangers.
/// </summary>
[ApiController]
[Route("api/demo")]
[EnableCors("demo")]
[Produces("application/json")]
public class DemoController : ControllerBase
{
    private readonly ErpOptions _erp;
    private readonly DemoOptions _demo;
    private readonly AppDbContext _db;
    private readonly SecretHasher _hasher;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<DemoController> _logger;
    private readonly IAdminUserService _users;
    private readonly IDataProtector _secrets;

    public DemoController(IOptions<ErpOptions> erp, IOptions<DemoOptions> demo, AppDbContext db,
                          SecretHasher hasher, IHttpClientFactory http, ILogger<DemoController> logger,
                          IAdminUserService users, IDataProtectionProvider dp)
    {
        _erp = erp.Value;
        _demo = demo.Value;
        _db = db;
        _hasher = hasher;
        _http = http;
        _logger = logger;
        _users = users;
        _secrets = dp.CreateProtector(AdminController.SecretPurpose);
    }

    /// <summary>
    /// Create a demo account: a portal user (log in with the e-mail and password) with its
    /// own fictional company and API credentials. The user is signed in to the portal,
    /// where the credentials and Swagger are available. Everything expires after the demo
    /// period.
    /// </summary>
    /// <response code="200">The demo account was created.</response>
    /// <response code="400">Invalid input or the bot check failed.</response>
    /// <response code="429">Too many sign-ups from this address.</response>
    [HttpPost("signup")]
    [EnableRateLimiting("signup")]
    [ProducesResponseType(typeof(DemoSignupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Signup([FromBody] DemoSignupRequest request)
    {
        if (!_erp.IsDemo)
            return NotFound();

        // Honeypot: a field people never see. Pretend success so bots learn nothing.
        if (!string.IsNullOrEmpty(request.Website))
            return Ok(new DemoSignupResponse("demo-" + Guid.NewGuid().ToString("N")[..12], "", DateTime.UtcNow));

        if (!IsValidEmail(request.Email))
            return BadRequest(new { error = "Angiv en gyldig e-mailadresse." });
        if ((request.Password ?? "").Length < 8)
            return BadRequest(new { error = "Adgangskoden skal være mindst 8 tegn." });

        var email = request.Email.Trim().ToLowerInvariant();
        if (await _users.FindAsync(email) != null)
            return Conflict(new { error = "Der findes allerede en konto med denne e-mail. Log ind i stedet." });

        if (!string.IsNullOrEmpty(_demo.TurnstileSecret) && !await TurnstileOk(request.TurnstileToken))
            return BadRequest(new { error = "Bot-tjekket fejlede. Prøv igen." });

        var company = (request.Company ?? "").Trim();
        if (company.Length > 100)
            company = company[..100];

        var tenant = new ApiTenant
        {
            Id = Guid.NewGuid(),
            Name = $"Demo – {(company == "" ? "(intet firma)" : company)} <{email}>",
            IsActive = true,
        };
        var clientId = "demo-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var expires = DateTime.UtcNow.AddDays(_demo.AccountLifetimeDays);

        _db.Tenants.Add(tenant);
        _db.Clients.Add(new ApiClient
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            ClientId = clientId,
            ClientSecretHash = _hasher.Hash(secret),
            // Reversible copy so the user can see the secret again under "My access".
            ClientSecretEnc = _secrets.Protect(secret),
            IsActive = true,
            ExpiresAt = expires,
        });
        await _db.SaveChangesAsync();

        var (user, error) = await _users.CreateAsync(email, request.Password!, email, "viewer", new List<string> { clientId });
        if (user == null)
        {
            _logger.LogWarning("Demo user for {ClientId} could not be created: {Error}", clientId, error);
            return BadRequest(new { error = error ?? "Kontoen kunne ikke oprettes." });
        }
        await AdminController.SignInAsync(HttpContext, user.Username);

        _logger.LogInformation("Demo account {ClientId} created, expires {Expires:u}", clientId, expires);
        return Ok(new DemoSignupResponse(clientId, secret, expires));
    }

    private static bool IsValidEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email) && email.Length <= 200 && MailAddress.TryCreate(email.Trim(), out _);

    private async Task<bool> TurnstileOk(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        try
        {
            var form = new Dictionary<string, string> { ["secret"] = _demo.TurnstileSecret!, ["response"] = token };
            var ip = ClientIp.Of(HttpContext);
            if (ip != null)
                form["remoteip"] = ip;

            using var response = await _http.CreateClient().PostAsync(
                "https://challenges.cloudflare.com/turnstile/v0/siteverify", new FormUrlEncodedContent(form));
            var result = await response.Content.ReadFromJsonAsync<TurnstileResult>();
            return result?.Success == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Turnstile verification failed");
            return false;
        }
    }

    private sealed record TurnstileResult(bool Success);
}

public class DemoSignupRequest
{
    [Required, MaxLength(200)]
    public string Email { get; set; } = "";

    /// <summary>Password for the portal login (at least 8 characters).</summary>
    [Required, MaxLength(200)]
    public string? Password { get; set; }

    [MaxLength(200)]
    public string? Company { get; set; }

    /// <summary>Cloudflare Turnstile token from the sign-up form (required when Turnstile is configured).</summary>
    public string? TurnstileToken { get; set; }

    /// <summary>Leave empty. Bots fill it in.</summary>
    public string? Website { get; set; }
}

public record DemoSignupResponse(string ClientId, string ClientSecret, DateTime ExpiresAt);

/// <summary>"Demo" configuration section.</summary>
public class DemoOptions
{
    public const string Section = "Demo";

    public int AccountLifetimeDays { get; set; } = 14;

    /// <summary>Cloudflare Turnstile secret key. When set, sign-up requires a valid token.</summary>
    public string? TurnstileSecret { get; set; }

    /// <summary>Origins allowed to call the sign-up endpoint from a browser, e.g. https://softcode.dk.</summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>Sign-ups allowed per IP address per hour.</summary>
    public int SignupsPerHour { get; set; } = 3;
}
