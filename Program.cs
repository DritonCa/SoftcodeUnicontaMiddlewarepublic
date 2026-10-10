using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SoftcodeUnicontaMiddleware.Data;
using SoftcodeUnicontaMiddleware.Filters;
using SoftcodeUnicontaMiddleware.Controllers;
using SoftcodeUnicontaMiddleware.Services;
using SoftcodeUnicontaMiddleware.Services.Erp;
using SoftcodeUnicontaMiddleware.Services.Erp.Demo;
using SoftcodeUnicontaMiddleware.UnicontaService;
using Microsoft.OpenApi;
using System.Reflection;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ----------------------------------------------------
// SERVICES
// ----------------------------------------------------

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHttpClient();

// Which ERP the API talks to: a real Uniconta company, or fictional demo data.
builder.Services.Configure<ErpOptions>(builder.Configuration.GetSection(ErpOptions.Section));
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection(DemoOptions.Section));
var erpOptions = builder.Configuration.GetSection(ErpOptions.Section).Get<ErpOptions>() ?? new ErpOptions();
var demoOptions = builder.Configuration.GetSection(DemoOptions.Section).Get<DemoOptions>() ?? new DemoOptions();
if (erpOptions.IsDemo)
{
    builder.Services.AddSingleton<DemoErpStore>();
    builder.Services.AddScoped<IErpGateway, DemoGateway>();
}
else
{
    builder.Services.AddScoped<IErpGateway, UnicontaGateway>();
}

// Only trust Cloudflare's client-IP header when the origin accepts nothing but Cloudflare.
ClientIp.TrustCloudflareHeader = builder.Configuration.GetValue<bool>("Network:TrustCloudflareHeader");

builder.Services.AddCors(options =>
    options.AddPolicy("demo", policy => policy
        .WithOrigins(demoOptions.AllowedOrigins)
        .WithMethods("POST")
        .WithHeaders("Content-Type")));

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SoftCode Uniconta Middleware",
        Version = "v1",
        Description = erpOptions.IsDemo
            ? "DEMO: fictional data, nothing reaches a real Uniconta company. Log in with the client id and secret from your demo account; the request body may be empty."
            : "REST API between a webshop or CMS and Uniconta ERP."
    });

    var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xml))
        options.IncludeXmlComments(xml);

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "The access_token from POST /api/auth/login."
    });
    options.AddSecurityDefinition("ClientId", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Client-Id",
        Description = "Client id (only needed for /api/auth/login)."
    });
    options.AddSecurityDefinition("ClientSecret", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Client-Secret",
        Description = "Client secret (only needed for /api/auth/login)."
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
        [new OpenApiSecuritySchemeReference("ClientId", document)] = [],
        [new OpenApiSecuritySchemeReference("ClientSecret", document)] = [],
    });
});

builder.Services.AddMemoryCache();

// ✅ REQUIRED for UnicontaServiceClientFactory
builder.Services.AddHttpContextAccessor();

// 🔐 Fail fast on missing or placeholder secrets before anything else starts.
// A predictable JWT key or client-secret pepper is a real vulnerability, so the
// app refuses to boot until real values are configured.
var jwtKey = StartupSecrets.Require(builder.Configuration, "Jwt:Key", 32);
StartupSecrets.Require(builder.Configuration, "Auth:SecretPepper", 16);

// 🔐 JWT AUTH
var jwt = builder.Configuration.GetSection("Jwt");

var key = Encoding.UTF8.GetBytes(jwtKey);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(key)
        };
    })
    // 🍪 Cookie scheme for the browser order-log interface only (JWT stays the
    // default, so the API clients are unaffected).
    .AddCookie(SoftcodeUnicontaMiddleware.Controllers.AdminController.CookieScheme, options =>
    {
        options.Cookie.Name         = "sc_admin";
        options.Cookie.HttpOnly     = true;
        options.Cookie.SameSite     = SameSiteMode.Lax;
        // Always, not SameAsRequest: the app sits behind a TLS-terminating proxy and
        // therefore sees plain HTTP, so SameAsRequest left the session cookie without
        // the Secure flag even though the browser was on HTTPS. The admin is reachable
        // over HTTPS only, so the flag can be unconditional.
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan      = TimeSpan.FromHours(8);
        options.SlidingExpiration   = true;
        // Return status codes instead of redirecting (the UI is a single page).
        options.Events.OnRedirectToLogin        = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });

builder.Services.AddAuthorization();

builder.Services.AddScoped<UnicontaServiceClientFactory>();

// Connection string comes from configuration (appsettings / env / user-secrets),
// falling back to a local dev SQLite file. Never hard-code infrastructure paths.
var connectionString = builder.Configuration.GetConnectionString("AppDb")
    ?? "Data Source=softcode_api.db";

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlite(connectionString);
});


builder.Services.AddSingleton<SecretHasher>();
builder.Services.AddScoped<IClientAuthService, ClientAuthService>();
builder.Services.AddScoped<ClientAuthFilter>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<IUnicontaCredentialStore, MemoryUnicontaCredentialStore>();
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(
        new DirectoryInfo(Path.Combine(
            builder.Environment.ContentRootPath,
            "dataprotection-keys")))
    .SetApplicationName("SoftcodeUnicontaMiddleware");
builder.Services.AddScoped<IRefreshTokenStore, MemoryRefreshTokenStore>();
// NOTE: AddHttpContextAccessor() is already registered above (line ~25); duplicate removed.
builder.Services.AddScoped<IAuditLogger, AuditLogger>();
builder.Services.Configure<SoftcodeUnicontaMiddleware.Services.OrderOptions>(
    builder.Configuration.GetSection(SoftcodeUnicontaMiddleware.Services.OrderOptions.Section));
builder.Services.AddScoped<SoftcodeUnicontaMiddleware.Services.OrderService>();
builder.Services.AddSingleton<SoftcodeUnicontaMiddleware.Services.IOrderLogger, SoftcodeUnicontaMiddleware.Services.OrderLogger>();

// Order-log admin interface
builder.Services.AddScoped<SoftcodeUnicontaMiddleware.Services.IAdminUserService, SoftcodeUnicontaMiddleware.Services.AdminUserService>();
builder.Services.AddSingleton<SoftcodeUnicontaMiddleware.Services.OrderLogReader>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("signup", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"signup:{ClientIp.Of(context) ?? "unknown"}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, demoOptions.SignupsPerHour),
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));

    options.AddPolicy("auth", context =>
    {
        var ip = ClientIp.Of(context) ?? "unknown";
        var clientId = context.Request.Headers["X-Client-Id"].FirstOrDefault();

        var key = string.IsNullOrWhiteSpace(clientId)
            ? $"auth:ip:{ip}"
            : $"auth:client:{clientId}:ip:{ip}";

        return RateLimitPartition.GetSlidingWindowLimiter(
            key,
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 5,               // very strict
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 1,
                QueueLimit = 0
            });
    });
});


var app = builder.Build();

// ----------------------------------------------------
// PIPELINE
// ----------------------------------------------------

// CORS first, so a browser's preflight request never uses up the sign-up quota.
app.UseCors();
app.UseRateLimiter();

// Swagger is always on in development; elsewhere only when "Swagger:Enabled" is true
// (the public demo), so a production install does not advertise its API.
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.DocumentTitle = "SoftCode Uniconta Middleware";
        options.RoutePrefix = "swagger";
    });
}

app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<SoftcodeUnicontaMiddleware.Middleware.ApiExceptionMiddleware>();

app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SoftcodeUnicontaMiddleware.Data.AppDbContext>();
    var hasher = scope.ServiceProvider.GetRequiredService<SoftcodeUnicontaMiddleware.Services.SecretHasher>();
    db.Database.Migrate();

    // AdminUsers is managed outside EF migrations (kept isolated from the security
    // schema) — ensure it exists on startup.
    db.Database.ExecuteSqlRaw(
        @"CREATE TABLE IF NOT EXISTS ""AdminUsers"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_AdminUsers"" PRIMARY KEY AUTOINCREMENT,
            ""Username"" TEXT NOT NULL,
            ""PasswordHash"" TEXT NOT NULL,
            ""CreatedAt"" TEXT NOT NULL,
            ""LastLoginAt"" TEXT NULL
          );");
    db.Database.ExecuteSqlRaw(
        @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_AdminUsers_Username"" ON ""AdminUsers"" (""Username"");");

    // Roles, e-mail and per-company access, added to AdminUsers after the table
    // shipped. SQLite has no ADD COLUMN IF NOT EXISTS, so each one is attempted and
    // the "duplicate column" error ignored — the same pattern as ClientSecretEnc
    // below. Existing rows default to an active admin with access to everything,
    // which is what the single first-run login already was.
    foreach (var column in new[]
             {
                 @"""Email"" TEXT NULL",
                 @"""Role"" TEXT NOT NULL DEFAULT 'admin'",
                 @"""AllowedClientIds"" TEXT NULL",
                 @"""IsActive"" INTEGER NOT NULL DEFAULT 1"
             })
    {
        try
        {
            db.Database.ExecuteSqlRaw($@"ALTER TABLE ""AdminUsers"" ADD COLUMN {column};");
        }
        catch
        {
            /* column already exists */
        }
    }

    // ClientSecretEnc (reversibly-encrypted secret for the admin Companies view) is
    // added out-of-migration; SQLite has no ADD COLUMN IF NOT EXISTS, so ignore the
    // "duplicate column" error on subsequent boots.
    foreach (var column in new[] { @"""ClientSecretEnc"" TEXT NULL", @"""ExpiresAt"" TEXT NULL" })
    {
        try
        {
            db.Database.ExecuteSqlRaw($@"ALTER TABLE ""Clients"" ADD COLUMN {column};");
        }
        catch
        {
            /* column already exists */
        }
    }

    SoftcodeUnicontaMiddleware.Data.DbSeeder.Seed(db, hasher);
}

app.Run();
