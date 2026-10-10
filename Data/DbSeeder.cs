using SoftcodeUnicontaMiddleware.Data.Entities;
using SoftcodeUnicontaMiddleware.Services;

namespace SoftcodeUnicontaMiddleware.Data
{
    public static class DbSeeder
    {
        public static void Seed(AppDbContext db, SecretHasher hasher)
        {
            // Prevent double-seeding
            if (db.Tenants.Any())
                return;

            // 1️⃣ Create tenant
            var tenant = new ApiTenant
            {
                Id = Guid.NewGuid(),
                Name = "Demo Customer",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            // 2️⃣ Create client credentials
            // Random per installation: a fixed secret in a public repository would let
            // anyone log in to every install that never changed it.
            var clientSecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

            var client = new ApiClient
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                ClientId = "demo-client",
                ClientSecretHash = hasher.Hash(clientSecret),
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            db.Tenants.Add(tenant);
            db.Clients.Add(client);
            db.SaveChanges();

            // Helpful for first run
            Console.WriteLine("==== API CLIENT SEEDED (shown once — store it now) ====");
            Console.WriteLine($"ClientId: demo-client");
            Console.WriteLine($"ClientSecret: {clientSecret}");
        }
    }
}
