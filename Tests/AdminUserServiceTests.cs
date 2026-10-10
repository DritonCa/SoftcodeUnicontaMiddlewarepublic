using Microsoft.EntityFrameworkCore;
using SoftcodeUnicontaMiddleware.Data;
using SoftcodeUnicontaMiddleware.Data.Entities;
using SoftcodeUnicontaMiddleware.Services;
using Xunit;

namespace SoftcodeUnicontaMiddleware.Tests;

/// <summary>
/// The first-run admin and the customer (viewer) accounts created by the demo sign-up
/// must not get in each other's way.
/// </summary>
public class AdminUserServiceTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task First_admin_can_be_created_when_only_customers_exist()
    {
        var service = new AdminUserService(NewDb());
        var (viewer, _) = await service.CreateAsync("kunde@example.com", "hemmelig-123", "kunde@example.com", "viewer", new[] { "demo-1" });
        Assert.NotNull(viewer);

        var admin = await service.CreateFirstAsync("driton", "a-long-password");

        Assert.NotNull(admin);
        Assert.True(admin!.IsAdmin);
    }

    [Fact]
    public async Task Only_one_first_admin()
    {
        var service = new AdminUserService(NewDb());
        Assert.NotNull(await service.CreateFirstAsync("driton", "a-long-password"));

        Assert.Null(await service.CreateFirstAsync("someone-else", "another-password"));
    }

    [Fact]
    public async Task Customer_only_sees_its_own_company()
    {
        var service = new AdminUserService(NewDb());
        var (viewer, _) = await service.CreateAsync("kunde@example.com", "hemmelig-123", null, "viewer", new[] { "demo-1" });

        Assert.Equal(new[] { "demo-1" }, viewer!.AllowedCompanies());
        Assert.False(viewer.IsAdmin);
    }

    [Fact]
    public async Task Customer_needs_a_company()
    {
        var service = new AdminUserService(NewDb());
        var (viewer, error) = await service.CreateAsync("kunde@example.com", "hemmelig-123", null, "viewer", Array.Empty<string>());

        Assert.Null(viewer);
        Assert.NotNull(error);
    }
}
