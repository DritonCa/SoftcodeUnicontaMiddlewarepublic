using Microsoft.EntityFrameworkCore;
using SoftcodeUnicontaMiddleware.Data;
using SoftcodeUnicontaMiddleware.Data.Entities;
using SoftcodeUnicontaMiddleware.Services;
using Xunit;

namespace SoftcodeUnicontaMiddleware.Tests;

public class ClientAuthServiceTests
{
    private static readonly SecretHasher Hasher = new("test-pepper");

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())   // isolated per test
            .Options;
        return new AppDbContext(options);
    }

    private static AppDbContext SeedClient(
        string clientId = "c1",
        string secret = "correct-secret",
        bool clientActive = true,
        bool tenantActive = true,
        DateTime? expiresAt = null)
    {
        var db = NewDb();
        var tenant = new ApiTenant { Id = Guid.NewGuid(), Name = "T", IsActive = tenantActive };
        db.Tenants.Add(tenant);
        db.Clients.Add(new ApiClient
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Tenant = tenant,
            ClientId = clientId,
            ClientSecretHash = Hasher.Hash(secret),
            IsActive = clientActive,
            ExpiresAt = expiresAt
        });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task Returns_client_for_correct_secret()
    {
        var sut = new ClientAuthService(SeedClient(), Hasher);
        var result = await sut.ValidateAsync("c1", "correct-secret");
        Assert.NotNull(result);
        Assert.Equal("c1", result!.ClientId);
    }

    [Fact]
    public async Task Returns_null_for_wrong_secret()
    {
        var sut = new ClientAuthService(SeedClient(), Hasher);
        Assert.Null(await sut.ValidateAsync("c1", "wrong-secret"));
    }

    [Fact]
    public async Task Returns_null_for_unknown_client()
    {
        var sut = new ClientAuthService(SeedClient(), Hasher);
        Assert.Null(await sut.ValidateAsync("does-not-exist", "correct-secret"));
    }

    [Fact]
    public async Task Returns_null_for_inactive_client()
    {
        var sut = new ClientAuthService(SeedClient(clientActive: false), Hasher);
        Assert.Null(await sut.ValidateAsync("c1", "correct-secret"));
    }

    [Fact]
    public async Task Returns_null_for_inactive_tenant()
    {
        var sut = new ClientAuthService(SeedClient(tenantActive: false), Hasher);
        Assert.Null(await sut.ValidateAsync("c1", "correct-secret"));
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("c1", "")]
    [InlineData(" ", " ")]
    public async Task Returns_null_for_empty_input(string clientId, string secret)
    {
        var sut = new ClientAuthService(SeedClient(), Hasher);
        Assert.Null(await sut.ValidateAsync(clientId, secret));
    }

    [Fact]
    public async Task Expired_demo_client_is_rejected()
    {
        var sut = new ClientAuthService(SeedClient(expiresAt: DateTime.UtcNow.AddMinutes(-1)), Hasher);
        Assert.Null(await sut.ValidateAsync("c1", "correct-secret"));
    }

    [Fact]
    public async Task Demo_client_works_until_it_expires()
    {
        var sut = new ClientAuthService(SeedClient(expiresAt: DateTime.UtcNow.AddDays(1)), Hasher);
        Assert.NotNull(await sut.ValidateAsync("c1", "correct-secret"));
    }
}
