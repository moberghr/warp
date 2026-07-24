using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warp.Core;
using Warp.Provider.PostgreSql;
using Warp.Test.Shared;
using Warp.Test.Shared.Entities;

// One-shot schema provisioner for the Aspire demo. Runs to COMPLETION before the web + worker start
// (Aspire WaitForCompletion), so neither races a not-yet-created table. It owns schema creation +
// product-catalog seeding; the web app only registers recurring-job definitions afterwards.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddServices(builder.Configuration);
builder.Services.AddWarp<TestContext>(o => o.UsePostgreSql());

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();

try
{
    await using var scope = host.Services.CreateAsyncScope();
    var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

    // Set WARP_DEMO_PRESERVE_DB=1 to skip the wipe (the worker sets it; the migrator does not, so a
    // normal run wipes + recreates a fresh schema each time).
    var preserve = string.Equals(Environment.GetEnvironmentVariable("WARP_DEMO_PRESERVE_DB"), "1", StringComparison.Ordinal);
    if (!preserve)
    {
        await ctx.Database.EnsureDeletedAsync();
    }

    await ctx.Database.EnsureCreatedAsync();

    // Seed the shop catalog (some SKUs start below the reorder threshold so the low-stock monitor has
    // something to report immediately).
    if (!await ctx.Products.AnyAsync())
    {
        ctx.Products.AddRange(
            new Product { Sku = "SKU-TEE", Name = "T-Shirt", Stock = 40, Price = 24.99m },
            new Product { Sku = "SKU-MUG", Name = "Mug", Stock = 8, Price = 12.50m },
            new Product { Sku = "SKU-CAP", Name = "Cap", Stock = 3, Price = 19.00m },
            new Product { Sku = "SKU-BAG", Name = "Tote Bag", Stock = 25, Price = 39.90m },
            new Product { Sku = "SKU-PEN", Name = "Pen", Stock = 2, Price = 3.25m });
        await ctx.SaveChangesAsync();
    }

    logger.LogInformation("[migrator] schema provisioned + catalog seeded");
}
catch (Exception ex)
{
    // Surface as a non-zero exit so Aspire's WaitForCompletion sees the migrator failed and the web +
    // worker never start against a half-provisioned schema. Rethrow WITH context (not a bare log +
    // rethrow, which S2139 flags) — the generic host logs the unhandled exception on the way out.
    throw new InvalidOperationException("[migrator] schema provisioning failed", ex);
}
