using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.CircuitBreaker;
using Warp.Core.Data.Converters;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;

namespace Warp.Tests.Core;

// How Warp's own columns are stored is a contract: the provider claim SQL bakes literals of a fixed
// type in, and WarpServerContext mirrors column NAMES only — it never replays the consumer's
// ConfigureConventions — so a convention that retypes a Warp column diverges the two contexts and
// nothing executes. ApplyWarpModel pins enums, DateTime and Guid on its own entities for that
// reason; these are the rot guards, including the non-bleed guarantee for the consumer's own types.
// Model-build only, so both providers are asserted without a container (§4.2).
[Trait("Category", "NoDb")]
public class StorageTypeConventionTests
{
    private const string PostgresConnection = "Host=dummy;Database=warp";
    private const string SqlServerConnection = "Server=dummy;Database=warp";

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarpEnumProperties_AreStoredAsInteger_WhenNoConventionApplied(bool sqlServer)
    {
        // TestContext takes an optional schema argument, so it is built directly rather than through
        // the shared Activator helper.
        var builder = new DbContextOptionsBuilder<TestContext>();
        Provider(builder, sqlServer).UseSnakeCaseNamingConvention();
        using var context = new TestContext(builder.Options);

        WarpProperties(context.Model)
            .Where(x => IsEnum(x.Property))
            .Where(x => x.Property.GetProviderClrType() != typeof(int))
            .Select(x => x.Describe())
            .ShouldBeEmpty();
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarpEnumProperties_AreStoredAsInteger_WhenConsumerRetypesWarpColumns(bool sqlServer)
    {
        using var context = Context<HostileConventionContext>(sqlServer);

        WarpProperties(context.Model)
            .Where(x => IsEnum(x.Property))
            .Where(x => x.Property.GetProviderClrType() != typeof(int))
            .Select(x => x.Describe())
            .ShouldBeEmpty();
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarpEnumProperty_IsStoredAsInteger_WhenTheEntityDeclaresNoExplicitPin(bool sqlServer)
    {
        using var context = Context<UnpinnedWarpEntityContext>(sqlServer);

        // Every Warp entity currently pins its enums explicitly at the declaration, so the tests above
        // pass on those pins alone and cannot see the sweep. This context registers a Warp entity type
        // WITHOUT Warp's own configuration — the case the sweep exists to backstop, i.e. a new entity
        // whose author forgot the explicit pin — so it fails if the enum arm of the sweep is removed.
        var state = context.Model.FindEntityType(typeof(CircuitBreakerState))!
            .FindProperty(nameof(CircuitBreakerState.State))!;

        state.GetProviderClrType().ShouldBe(typeof(int));
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarpDateTimeProperties_KeepUtcConversion_WhenConsumerRetypesWarpColumns(bool sqlServer)
    {
        using var context = Context<HostileConventionContext>(sqlServer);

        // The UTC converter round-trips DateTime to DateTime (§5.7): a provider type of anything else
        // means the consumer's conversion reached Warp's column and took the Kind stamp with it.
        WarpProperties(context.Model)
            .Where(x => Unwrap(x.Property) == typeof(DateTime))
            .Where(x => UnwrapType(x.Property.GetValueConverter()?.ProviderClrType) != typeof(DateTime))
            .Select(x => x.Describe())
            .ShouldBeEmpty();
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarpGuidProperties_KeepNativeType_WhenConsumerRetypesWarpColumns(bool sqlServer)
    {
        using var context = Context<HostileConventionContext>(sqlServer);

        WarpProperties(context.Model)
            .Where(x => Unwrap(x.Property) == typeof(Guid))
            .Where(x => x.Property.GetProviderClrType() is not null)
            .Select(x => x.Describe())
            .ShouldBeEmpty();
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsumerProperties_KeepTheirConventionTypes_WhenWarpPinsItsOwn(bool sqlServer)
    {
        using var context = Context<HostileConventionContext>(sqlServer);
        var consumer = context.Model.FindEntityType(typeof(ConsumerRow))!;

        // Warp pinning its own storage must not reach across into the consumer's entities.
        consumer.FindProperty(nameof(ConsumerRow.Flavour))!.GetProviderClrType().ShouldBe(typeof(string));
        consumer.FindProperty(nameof(ConsumerRow.PlacedAt))!.GetProviderClrType().ShouldBe(typeof(long));
        consumer.FindProperty(nameof(ConsumerRow.Reference))!.GetProviderClrType().ShouldBe(typeof(string));
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ServerContext_MatchesUserContextColumnTypes_WhenConsumerRetypesWarpColumns(bool sqlServer)
    {
        var services = new ServiceCollection();
        services.AddDbContext<HostileConventionContext>(x => Provider(x, sqlServer).UseSnakeCaseNamingConvention());
        services.AddDbContext<WarpServerContext<HostileConventionContext>>(x => Provider(x, sqlServer));
        services.AddSingleton<IOptions<WarpConfiguration>>(Options.Create(new WarpConfiguration()));
        services.AddSingleton<IWarpServerModelNames>(sp =>
            new WarpServerModelNames<HostileConventionContext>(sp.GetRequiredService<IServiceScopeFactory>()));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var userModel = scope.ServiceProvider.GetRequiredService<HostileConventionContext>().Model;
        var serverModel = scope.ServiceProvider.GetRequiredService<WarpServerContext<HostileConventionContext>>().Model;

        // Both contexts read and write the same physical columns, so any disagreement here is the
        // "operator does not exist" failure the server tasks hit every tick.
        var divergent = WarpProperties(serverModel)
            .Select(x =>
                new
                {
                    x.Entity,
                    x.Property,
                    UserType = userModel.FindEntityType(x.Entity.ClrType)
                        ?.FindProperty(x.Property.Name)
                        ?.GetColumnType() ?? "absent from the user model",
                })
            .Where(x => !string.Equals(x.Property.GetColumnType(), x.UserType, StringComparison.Ordinal))
            .Select(x => $"{x.Entity.ClrType.Name}.{x.Property.Name}: server={x.Property.GetColumnType()} user={x.UserType}");

        divergent.ShouldBeEmpty();
    }

    private static DbContextOptionsBuilder Provider(DbContextOptionsBuilder builder, bool sqlServer)
    {
        return sqlServer
            ? builder.UseSqlServer(SqlServerConnection)
            : builder.UseNpgsql(PostgresConnection);
    }

    private static TContext Context<TContext>(bool sqlServer)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        Provider(builder, sqlServer).UseSnakeCaseNamingConvention();

        return (TContext)Activator.CreateInstance(typeof(TContext), builder.Options)!;
    }

    private static IEnumerable<WarpProperty> WarpProperties(IReadOnlyModel model)
    {
        return model.GetEntityTypes()
            .Where(x => x.ClrType.Assembly == typeof(Job).Assembly)
            .SelectMany(x => x.GetProperties()
                .Select(y =>
                    new WarpProperty(x, y)));
    }

    private static Type Unwrap(IReadOnlyProperty property)
    {
        return UnwrapType(property.ClrType)!;
    }

    // A nullable DateTime keeps its nullability through the converter, so the provider type of
    // UtcNullableDateTime is DateTime? — unwrap before comparing.
    private static Type? UnwrapType(Type? type)
    {
        return type is null ? null : Nullable.GetUnderlyingType(type) ?? type;
    }

    private static bool IsEnum(IReadOnlyProperty property)
    {
        return Unwrap(property).IsEnum;
    }

    private sealed record WarpProperty(IReadOnlyEntityType Entity, IReadOnlyProperty Property)
    {
        public string Describe()
        {
            var provider = Property.GetProviderClrType()?.Name ?? "native";

            return $"{Entity.ClrType.Name}.{Property.Name} store={Property.GetColumnType()} providerClr={provider}";
        }
    }
}

// A consuming context that retypes all three families Warp cares about, model-wide.
internal class HostileConventionContext : DbContext
{
    public HostileConventionContext(DbContextOptions<HostileConventionContext> options)
        : base(options)
    {
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Enum>().HaveConversion<string>();
        configurationBuilder.Properties<DateTime>().HaveConversion<long>();
        configurationBuilder.Properties<Guid>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ConsumerRow>();
        modelBuilder.ApplyWarpModel("warp");
    }
}

// A Warp entity type registered WITHOUT Warp's own configuration, so nothing pins its enum but the
// sweep — standing in for a future Warp entity whose author forgets the explicit pin.
internal class UnpinnedWarpEntityContext : DbContext
{
    public UnpinnedWarpEntityContext(DbContextOptions<UnpinnedWarpEntityContext> options)
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
        modelBuilder.Entity<CircuitBreakerState>().HasKey(x => x.GroupKey);
        modelBuilder.PinWarpStorageTypes();
    }
}

internal class ConsumerRow
{
    public int Id { get; set; }

    public State Flavour { get; set; }

    public DateTime PlacedAt { get; set; }

    public Guid Reference { get; set; }
}
