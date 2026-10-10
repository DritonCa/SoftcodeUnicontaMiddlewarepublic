using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SoftcodeUnicontaMiddleware.Data.Entities;
using SoftcodeUnicontaMiddleware.Filters;
using SoftcodeUnicontaMiddleware.Models.Auth;
using SoftcodeUnicontaMiddleware.Services;
using SoftcodeUnicontaMiddleware.Services.Erp;
using SoftcodeUnicontaMiddleware.UnicontaService;

namespace SoftcodeUnicontaMiddleware.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        // How long a refresh token (and the cached Uniconta credentials behind it)
        // stay valid. Kept in one place so the two TTLs can never drift apart.
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

        private readonly JwtTokenService _jwt;
        private readonly IUnicontaCredentialStore _store;
        private readonly IRefreshTokenStore _refreshStore;
        private readonly IMemoryCache _cache;
        private readonly ErpOptions _erp;

        public AuthController(
            JwtTokenService jwt,
            IUnicontaCredentialStore store,
            IRefreshTokenStore refreshStore,
            IMemoryCache cache,
            IOptions<ErpOptions> erp)
        {
            _jwt = jwt;
            _store = store;
            _refreshStore = refreshStore;
            _cache = cache;
            _erp = erp.Value;
        }

        /// <summary>
        /// Demo mode: the client credentials are all that is checked. Each demo client
        /// gets its own fictional company, derived from its tenant, so testers never see
        /// each other's orders. Nothing reaches a real Uniconta company.
        /// </summary>
        private IActionResult DemoLogin()
        {
            if (HttpContext.Items["ApiClient"] is not ApiClient client)
                return Unauthorized("Invalid client credentials");

            var companyId = (int)(BitConverter.ToUInt32(client.TenantId.ToByteArray(), 0) & 0x7FFFFFFF);
            var username = client.ClientId;

            _store.Store(new UnicontaCredentials
            {
                Username = username,
                EncryptedPassword = "demo",
                ApiKey = "demo",
                CompanyId = companyId
            }, SessionLifetime);

            return IssueTokens(username, companyId);
        }

        private IActionResult IssueTokens(string username, int companyId)
        {
            var refreshToken = _jwt.GenerateRefreshToken();
            _refreshStore.Store(new RefreshToken
            {
                Token = refreshToken,
                Username = username,
                CompanyId = companyId,
                ExpiresAt = DateTime.UtcNow.Add(SessionLifetime)
            });

            return Ok(new
            {
                access_token = _jwt.CreateToken(username, companyId),
                refresh_token = refreshToken,
                token_type = "Bearer"
            });
        }

        /// <summary>
        /// Log in with client credentials (X-Client-Id / X-Client-Secret headers) and the
        /// Uniconta user, password and API key in the body. Returns a short-lived JWT and a
        /// refresh token. In demo mode the body may be empty.
        /// </summary>
        [HttpPost("login")]
        [ServiceFilter(typeof(ClientAuthFilter))]
        [EnableRateLimiting("auth")]
        public async Task<IActionResult> Login(
            [FromBody] UnicontaLoginRequest? request)
        {
            if (_erp.IsDemo)
                return DemoLogin();

            request ??= new UnicontaLoginRequest();
            // 1️⃣ Validate input (basic safety)
            if (string.IsNullOrWhiteSpace(request.UserName) ||
                string.IsNullOrWhiteSpace(request.Password) ||
                string.IsNullOrWhiteSpace(request.ApiKey))
            {
                return BadRequest("Missing credentials");
            }

            // 2️⃣ Verify the credentials against Uniconta once.
            var client = new UnicontaServiceClient(
                request.UserName,
                request.Password,
                request.ApiKey,
                _cache);

            await client.InitializeAsync();

            // 3️⃣ Cache the Uniconta secrets server-side (encrypted). Subsequent API
            //     calls read them from here via UnicontaServiceClientFactory, so the
            //     password/API key never have to travel inside the access token.
            _store.Store(new UnicontaCredentials
            {
                Username = request.UserName,
                EncryptedPassword = request.Password,
                ApiKey = request.ApiKey,
                CompanyId = client.CompanyId
            }, SessionLifetime);

            // 4️⃣ Issue a rotating refresh token.
            var refreshToken = _jwt.GenerateRefreshToken();

            _refreshStore.Store(new RefreshToken
            {
                Token = refreshToken,
                Username = request.UserName,
                CompanyId = client.CompanyId,
                ExpiresAt = DateTime.UtcNow.Add(SessionLifetime)
            });

            // 5️⃣ Issue the access token (identity only – no secrets inside).
            var accessToken = _jwt.CreateToken(request.UserName, client.CompanyId);

            return Ok(new
            {
                access_token = accessToken,
                refresh_token = refreshToken,
                token_type = "Bearer"
            });
        }

        [HttpPost("refresh")]
        public IActionResult Refresh([FromBody] string refreshToken)
        {
            var stored = _refreshStore.Get(refreshToken);

            if (stored == null || stored.ExpiresAt < DateTime.UtcNow)
                return Unauthorized("Invalid refresh token");

            // A fresh access token is useless without the cached Uniconta credentials.
            // If they have already expired, force a full re-login instead of handing
            // out a token the API layer cannot honour.
            var credentials = _store.Get(stored.Username, stored.CompanyId);
            if (credentials == null)
                return Unauthorized("Session expired – please log in again");

            // Rotate the refresh token (single use) and slide the credential TTL so an
            // actively-refreshing session keeps its Uniconta secrets alive.
            _refreshStore.Revoke(refreshToken);
            _store.Store(credentials, SessionLifetime);

            var newRefreshToken = _jwt.GenerateRefreshToken();

            _refreshStore.Store(new RefreshToken
            {
                Token = newRefreshToken,
                Username = stored.Username,
                CompanyId = stored.CompanyId,
                ExpiresAt = DateTime.UtcNow.Add(SessionLifetime)
            });

            var newAccessToken = _jwt.CreateToken(stored.Username, stored.CompanyId);

            return Ok(new
            {
                access_token = newAccessToken,
                refresh_token = newRefreshToken,
                token_type = "Bearer"
            });
        }
    }
}
