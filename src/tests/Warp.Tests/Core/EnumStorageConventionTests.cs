using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Entities;
using Warp.Core.Enums;

namespace Warp.Tests.Core;

// Every enum property in the Warp model must store as an integer. The provider claim SQL bakes
// integer literals in ("current_state" = 1) and WarpServerContext mirrors column NAMES only — it
// never replays the consumer's ConfigureConventions — so a consuming context that declares a global
// enum-to-string conversion silently turns Warp's own columns into text and nothing executes.
// ApplyWarpModel pins the provider type explicitly on every one of them; these are the rot guards.
[Trait("Category", "NoDb")]
public class EnumStorageConventionTests
{
    [TimedFact]
    public void WarpEnumProperties_AreStoredAsInteger_WhenNoConventionApplied()
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseNpgsql("Host=dummy")
            .Options;

        using var context = new TestContext(options);

        NonIntegerEnumProperties(context.Model).ShouldBeEmpty();
    }

    [TimedFact]
    public void WarpEnumProperties_AreStoredAsInteger_WhenConsumerConvertsEnumsToStrings()
    {
        var options = new DbContextOptionsBuilder<EnumStringContext>()
            .UseNpgsql("Host=dummy")
            .UseSnakeCaseNamingConvention()
            .Options;

        using var context = new EnumStringContext(options);

        // The consumer's own entity proves the convention is actually in effect, so the assertion
        // below can't pass vacuously if EF changes how ConfigureConventions is applied.
        var consumerProperty = context.Model.FindEntityType(typeof(ConsumerRow))!.FindProperty(nameof(ConsumerRow.Flavour))!;
        consumerProperty.GetProviderClrType().ShouldBe(typeof(string));

        NonIntegerEnumProperties(context.Model).ShouldBeEmpty();
    }

    [TimedFact]
    public void ServerContext_StoresEnumsLikeUserContext_WhenConsumerConvertsEnumsToStrings()
    {
        var services = new ServiceCollection();
        services.AddDbContext<EnumStringContext>(x => x
            .UseNpgsql("Host=dummy")
            .UseSnakeCaseNamingConvention());
        services.AddDbContext<WarpServerContext<EnumStringContext>>(x => x.UseNpgsql("Host=dummy"));
        services.AddSingleton<IOptions<WarpConfiguration>>(Options.Create(new WarpConfiguration()));
        services.AddSingleton<IWarpServerModelNames>(sp =>
            new WarpServerModelNames<EnumStringContext>(sp.GetRequiredService<IServiceScopeFactory>()));

        using var provider = services.BuildServiceProvider();
        var userJob = provider.GetRequiredService<EnumStringContext>().Model.FindEntityType(typeof(Job))!;
        var serverJob = provider.GetRequiredService<WarpServerContext<EnumStringContext>>().Model.FindEntityType(typeof(Job))!;

        // The server context reads and writes the same physical columns, so a divergence in provider
        // type here is the 42883 "operator does not exist: text = integer" the server tasks hit.
        var divergent = serverJob.GetProperties()
            .Where(x => IsEnum(x))
            .Where(x => x.GetProviderClrType() != userJob.FindProperty(x.Name)!.GetProviderClrType())
            .Select(x => x.Name);

        divergent.ShouldBeEmpty();
    }

    private static List<string> NonIntegerEnumProperties(IReadOnlyModel model)
    {
        return
        [
            .. model.GetEntityTypes()
                .Where(x => x.ClrType.Assembly == typeof(Job).Assembly)
                .SelectMany(x => x.GetProperties()
                    .Where(y => IsEnum(y))
                    .Where(y => y.GetProviderClrType() != typeof(int))
                    .Select(y => $"{x.ClrType.Name}.{y.Name} -> {y.GetProviderClrType()?.Name ?? "unpinned"}")),
        ];
    }

    private static bool IsEnum(IReadOnlyProperty property)
    {
        return (Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType).IsEnum;
    }
}

internal class EnumStringContext : DbContext
{
    public EnumStringContext(DbContextOptions<EnumStringContext> options)
        : base(options)
    {
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Enum>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ConsumerRow>();
        modelBuilder.ApplyWarpModel("warp");
    }
}

internal class ConsumerRow
{
    public Guid Id { get; set; }

    public State Flavour { get; set; }
}
