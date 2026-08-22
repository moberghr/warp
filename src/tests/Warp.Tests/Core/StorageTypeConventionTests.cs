using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
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

        // Warp pinning its own storage must not reach across into the consumer's entities —
        // conversions and facets both keep applying there.
        consumer.FindProperty(nameof(ConsumerRow.Flavour))!.GetProviderClrType().ShouldBe(typeof(string));
        consumer.FindProperty(nameof(ConsumerRow.PlacedAt))!.GetValueConverter().ShouldBeOfType<TicksDateTimeConverter>();
        consumer.FindProperty(nameof(ConsumerRow.PlacedAt))!.GetValueComparer().ShouldBeOfType<CoarseDateTimeComparer>();
        consumer.FindProperty(nameof(ConsumerRow.Reference))!.GetProviderClrType().ShouldBe(typeof(string));
        consumer.FindProperty(nameof(ConsumerRow.Note))!.GetMaxLength().ShouldBe(50);
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarpScalarProperties_KeepNativeTypes_WhenConsumerRetypesWarpColumns(bool sqlServer)
    {
        using var context = Context<HostileConventionContext>(sqlServer);

        // The generalized reclaim: every non-whitelisted scalar goes back to native storage, so bool,
        // int, long and double conversions never reach Warp's columns either.
        WarpProperties(context.Model)
            .Where(x => !IsEnum(x.Property))
            .Where(x => Unwrap(x.Property) != typeof(DateTime))
            .Where(x => Unwrap(x.Property) != typeof(IReadOnlyList<TimeSpan>))
            .Where(x => x.Property.GetProviderClrType() is not null || x.Property.GetValueConverter() is not null)
            .Select(x => x.Describe())
            .ShouldBeEmpty();
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarpStringProperties_KeepWarpFacets_WhenConsumerAppliesFacetConventions(bool sqlServer)
    {
        using var context = Context<HostileConventionContext>(sqlServer);
        var job = context.Model.FindEntityType(typeof(Job))!;

        // Pre-convention facets land at property creation with the same configuration source as
        // Warp's own fluent calls, so ApplyWarpModel resets and re-declares: Warp's declared facets
        // survive, the consumer's model-wide ones do not — a global HaveMaxLength must never
        // silently truncate Job.Message, and a global HaveColumnType must never retype it.
        job.FindProperty(nameof(Job.Message))!.GetMaxLength().ShouldBeNull();
        job.FindProperty(nameof(Job.Message))!.FindAnnotation("Relational:ColumnType").ShouldBeNull();
        job.FindProperty(nameof(Job.Message))!.IsUnicode().ShouldBeNull();
        job.FindProperty(nameof(Job.Application))!.GetMaxLength().ShouldBe(200);
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetryScheduleConverter_SurvivesTheReclaim(bool sqlServer)
    {
        using var context = Context<HostileConventionContext>(sqlServer);
        var delivery = context.Model.FindEntityType(typeof(Warp.Core.Data.Entities.WebhookDelivery))!;

        // The reclaim wipes converters and the re-declaration restores Warp's own — losing this one
        // would silently break webhook retry-schedule persistence (§8.20 roundtrip mandate).
        delivery.FindProperty(nameof(Warp.Core.Data.Entities.WebhookDelivery.RetrySchedule))!
            .GetValueConverter()
            .ShouldNotBeNull();
    }

    [TimedFact]
    public void EnsureWarpStorageContract_Throws_WhenAFinalizingConventionRetypesAWarpColumn()
    {
        var options = new DbContextOptionsBuilder<FinalizingConventionContext>()
            .UseNpgsql(PostgresConnection)
            .Options;

        using var context = new FinalizingConventionContext(options);

        // A convention added via ConfigureConventions(c => c.Conventions.Add(...)) runs at model
        // finalization — after OnModelCreating and past every build-time pin. The boot guard is the
        // only line of defense, and it must name the property.
        var ex = Should.Throw<InvalidOperationException>(() => WarpModelGuard.EnsureWarpStorageContract(context));

        ex.Message.ShouldContain("Job.Kind");
    }

    [TimedFact]
    public void ExplicitFacetOverride_AfterApplyWarpModel_StillWins()
    {
        var options = new DbContextOptionsBuilder<EscapeHatchContext>()
            .UseNpgsql(PostgresConnection)
            .Options;

        using var context = new EscapeHatchContext(options);

        // The documented escape hatch: a deliberate per-property FACET override placed after
        // ApplyWarpModel in the consumer's OnModelCreating outranks the ownership pass, which only
        // neutralizes model-wide conventions. Conversions are not part of the hatch — the boot
        // guard rejects those.
        context.Model.FindEntityType(typeof(Job))!
            .FindProperty(nameof(Job.Message))!
            .GetMaxLength()
            .ShouldBe(123);
    }

    [TimedFact]
    public void EnsureWarpStorageContract_Throws_WhenAForeignDateTimeConverterIsPlacedOnAWarpColumn()
    {
        var options = new DbContextOptionsBuilder<ForeignDateTimeConverterContext>()
            .UseNpgsql(PostgresConnection)
            .Options;

        using var context = new ForeignDateTimeConverterContext(options);

        // A DateTime-to-DateTime converter has the right CLR shape but can carry local-time
        // semantics — the guard accepts Warp's own UTC converters by reference, nothing else.
        var ex = Should.Throw<InvalidOperationException>(() => WarpModelGuard.EnsureWarpStorageContract(context));

        ex.Message.ShouldContain("Job.CreateTime");
    }

    [TimedFact]
    public void EnsureWarpStorageContract_Throws_WhenARetryScheduleConverterIsReplaced()
    {
        var options = new DbContextOptionsBuilder<ForeignRetryScheduleConverterContext>()
            .UseNpgsql(PostgresConnection)
            .Options;

        using var context = new ForeignRetryScheduleConverterContext(options);

        var ex = Should.Throw<InvalidOperationException>(() => WarpModelGuard.EnsureWarpStorageContract(context));

        ex.Message.ShouldContain("WebhookDelivery.RetrySchedule");
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarpDateTimeProperties_DoNotKeepConsumerComparers(bool sqlServer)
    {
        using var context = Context<HostileConventionContext>(sqlServer);

        // A comparer welded on by Properties<T>().HaveConversion<TConv, TComparer>() would make
        // Warp's change tracking skip small updates (a requeue ScheduleTime reset, a heartbeat
        // bump) — the reclaim strips comparers along with converters.
        var comparer = context.Model.FindEntityType(typeof(Job))!
            .FindProperty(nameof(Job.CreateTime))!
            .GetValueComparer();

        comparer.ShouldNotBeOfType<CoarseDateTimeComparer>();
    }

    [TimedFact]
    public void ConfiguratorAddedDateTimeProperty_OnAWarpEntity_GetsPinnedAndPassesTheContract()
    {
        // A dedicated TContext: EF caches the built model per closed context type, so reusing
        // WarpServerContext<HostileConventionContext> here would silently get the model an earlier
        // test built WITHOUT the configurator.
        var services = new ServiceCollection();
        var configuration = new WarpConfiguration();
        configuration.EntityConfigurators.Add((modelBuilder, _) =>
            modelBuilder.Entity<Job>().Property<DateTime>("AddonAuditedAt"));
        services.AddDbContext<WarpServerContext<ConfiguratorHostContext>>(x => x.UseNpgsql(PostgresConnection));
        services.AddDbContext<ConfiguratorHostContext>(x => x.UseNpgsql(PostgresConnection));
        services.AddSingleton<IOptions<WarpConfiguration>>(Options.Create(configuration));
        services.AddSingleton<IWarpServerModelNames>(sp =>
            new WarpServerModelNames<ConfiguratorHostContext>(sp.GetRequiredService<IServiceScopeFactory>()));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var server = scope.ServiceProvider.GetRequiredService<WarpServerContext<ConfiguratorHostContext>>();

        // A configurator-added property lands after ApplyWarpModel's ownership pass; the re-pin
        // gives it Warp's UTC converter so the storage contract stays satisfiable for addons.
        var added = server.Model.FindEntityType(typeof(Job))!.FindProperty("AddonAuditedAt")!;
        added.GetValueConverter().ShouldNotBeNull();
        Should.NotThrow(() => WarpModelGuard.EnsureWarpStorageContract(server));
    }

    [TimedFact]
    public void EnsureWarpStorageContract_Passes_OnACleanModel()
    {
        using var context = Context<HostileConventionContext>(sqlServer: false);

        Should.NotThrow(() => WarpModelGuard.EnsureWarpStorageContract(context));
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
        configurationBuilder.Properties<DateTime>().HaveConversion<TicksDateTimeConverter, CoarseDateTimeComparer>();
        configurationBuilder.Properties<Guid>().HaveConversion<string>();
        configurationBuilder.Properties<bool>().HaveConversion<string>();
        configurationBuilder.Properties<int>().HaveConversion<string>();
        configurationBuilder.Properties<long>().HaveConversion<string>();
        configurationBuilder.Properties<double>().HaveConversion<decimal>();
        configurationBuilder.Properties<string>().HaveMaxLength(50).AreUnicode(false);
        configurationBuilder.Properties<string>().HaveColumnType("varchar(64)");
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

internal sealed class CoarseDateTimeComparer : Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<DateTime>
{
    public CoarseDateTimeComparer()
        : base((x, y) => x.Date == y.Date, x => x.Date.GetHashCode())
    {
    }
}

internal sealed class TicksDateTimeConverter : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>
{
    public TicksDateTimeConverter()
        : base(x => x.ToLocalTime(), x => x)
    {
    }
}

internal class ConfiguratorHostContext : DbContext
{
    public ConfiguratorHostContext(DbContextOptions<ConfiguratorHostContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyWarpModel("warp");
    }
}

internal class ForeignDateTimeConverterContext : DbContext
{
    public ForeignDateTimeConverterContext(DbContextOptions<ForeignDateTimeConverterContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyWarpModel("warp");
        modelBuilder.Entity<Job>().Property(x => x.CreateTime).HasConversion(new TicksDateTimeConverter());
    }
}

internal class ForeignRetryScheduleConverterContext : DbContext
{
    public ForeignRetryScheduleConverterContext(DbContextOptions<ForeignRetryScheduleConverterContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyWarpModel("warp");
        modelBuilder.Entity<Warp.Core.Data.Entities.WebhookDelivery>()
            .Property(x => x.RetrySchedule)
            .HasConversion(
                x => string.Join(";", x.Select(y => y.TotalSeconds)),
                x => x.Split(";", StringSplitOptions.RemoveEmptyEntries).Select(y => TimeSpan.FromSeconds(double.Parse(y, System.Globalization.CultureInfo.InvariantCulture))).ToList());
    }
}

internal class EscapeHatchContext : DbContext
{
    public EscapeHatchContext(DbContextOptions<EscapeHatchContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyWarpModel("warp");
        modelBuilder.Entity<Job>().Property(x => x.Message).HasMaxLength(123);
    }
}

// The one surface build-time ordering cannot beat: a runtime convention added by the host runs at
// model finalization, after OnModelCreating entirely. WarpModelGuard.EnsureWarpStorageContract is
// the guard for it.
internal class FinalizingConventionContext : DbContext
{
    public FinalizingConventionContext(DbContextOptions<FinalizingConventionContext> options)
        : base(options)
    {
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Conventions.Add(_ => new RetypeWarpColumnConvention());
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyWarpModel("warp");
    }

    private sealed class RetypeWarpColumnConvention : IModelFinalizingConvention
    {
        public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
        {
            modelBuilder.Metadata.FindEntityType(typeof(Job))
                ?.FindProperty(nameof(Job.Kind))
                ?.SetProviderClrType(typeof(string), fromDataAnnotation: false);
        }
    }
}

internal class ConsumerRow
{
    public int Id { get; set; }

    public State Flavour { get; set; }

    public DateTime PlacedAt { get; set; }

    public Guid Reference { get; set; }

    public string Note { get; set; } = string.Empty;
}
