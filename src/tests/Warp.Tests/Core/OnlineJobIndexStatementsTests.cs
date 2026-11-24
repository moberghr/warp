using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Shouldly;
using Warp.Core.Data;

namespace Warp.Tests.Core;

// The 8.0 migration recreates the job table's four partial indexes (rule 6.15); EF drops and creates them
// inside the migration's transaction, which locks a large job table for every build. The providers'
// RecreateWarpJobIndexesOnline replaces those lines. Its SQL is built from the migration's target model,
// never from hard-coded names, so these pin what it builds for both namings and for a snapshot-shaped model.
[Trait("Category", "NoDb")]
public class OnlineJobIndexStatementsTests
{
    [Fact]
    public void RecreateWarpJobIndexesOnline_PostgreSql_BuildsEachIndexConcurrentlyOutsideTheTransaction()
    {
        var options = new DbContextOptionsBuilder<TestContext>().UseNpgsql("Host=dummy").UseSnakeCaseNamingConvention().Options;
        using var context = new TestContext(options);
        var migrationBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");

        Warp.Provider.PostgreSql.WarpMigrationBuilderExtensions.RecreateWarpJobIndexesOnline(migrationBuilder, context.Model);

        var operations = migrationBuilder.Operations.Cast<SqlOperation>().ToList();
        operations.Count.ShouldBe(16, "four statements for each of the four indexes");
        operations.ShouldAllBe(x => x.SuppressTransaction);
        operations.Take(4).Select(x => x.Sql).ShouldBe(
        [
            "DROP INDEX CONCURRENTLY IF EXISTS \"warp\".\"ix_job_kind_current_state_queue_schedule_time_warp_new\";",
            "CREATE INDEX CONCURRENTLY \"ix_job_kind_current_state_queue_schedule_time_warp_new\" ON \"warp\".\"job\" (\"kind\", \"current_state\", \"queue\", \"schedule_time\") WHERE \"current_state\" = 1;",
            "DROP INDEX CONCURRENTLY IF EXISTS \"warp\".\"ix_job_kind_current_state_queue_schedule_time\";",
            "ALTER INDEX \"warp\".\"ix_job_kind_current_state_queue_schedule_time_warp_new\" RENAME TO \"ix_job_kind_current_state_queue_schedule_time\";",
        ]);
        operations.ShouldContain(x => x.Sql.Contains("ON \"warp\".\"job\" (\"expire_at\") WHERE \"expire_at\" IS NOT NULL;", StringComparison.Ordinal));
    }

    [Fact]
    public void RecreateWarpJobIndexesOnline_SqlServer_ReplacesEachIndexInPlaceOnlineWhereTheEditionAllows()
    {
        var options = new DbContextOptionsBuilder<TestContext>().UseSqlServer("Server=dummy").Options;
        using var context = new TestContext(options);
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");

        Warp.Provider.SqlServer.WarpMigrationBuilderExtensions.RecreateWarpJobIndexesOnline(migrationBuilder, context.Model);

        var operations = migrationBuilder.Operations.Cast<SqlOperation>().ToList();
        operations.Count.ShouldBe(4);
        operations.ShouldAllBe(x => x.SuppressTransaction);
        var claim = operations[0].Sql;
        claim.ShouldContain("N'CREATE INDEX [IX_Job_Kind_CurrentState_Queue_ScheduleTime] ON [warp].[Job] ([Kind], [CurrentState], [Queue], [ScheduleTime]) WHERE \"CurrentState\" = 1 WITH (DROP_EXISTING = '");
        claim.ShouldContain("SERVERPROPERTY('EngineEdition')");
        claim.ShouldContain("ONLINE = ");
        operations[3].Sql.ShouldContain("ON [warp].[Job] ([ExpireAt]) WHERE \"ExpireAt\" IS NOT NULL");
    }

    [Fact]
    public void FindPartial_AModelShapedLikeAMigrationSnapshot_ReadsNamesColumnsAndFiltersFromIt()
    {
        // A migration's TargetModel comes from its designer file: entity types named by string, the names,
        // columns and filters as annotations. Nothing here may depend on the CLR type.
        var modelBuilder = new ModelBuilder();
        modelBuilder.Entity("Warp.Core.Entities.Job", x =>
        {
            x.Property<Guid>("Id");
            x.Property<int>("Kind").HasColumnName("k");
            x.Property<int>("CurrentState").HasColumnName("state");
            x.Property<string>("Queue").HasColumnName("q");
            x.Property<DateTime>("ScheduleTime").HasColumnName("due");
            x.HasKey("Id");
            x.HasIndex("Kind", "CurrentState", "Queue", "ScheduleTime").HasDatabaseName("claim_idx").HasFilter("\"state\" = 1");
            x.HasIndex("CurrentState", "ScheduleTime").HasDatabaseName("scheduled_idx");
            x.ToTable("jobs", "ops");
        });

        var indexes = WarpJobIndexes.FindPartial(modelBuilder.FinalizeModel());

        var claim = indexes.ShouldHaveSingleItem("an index without a filter is not one to recreate");
        claim.ShouldBe(new WarpJobIndex("claim_idx", "ops", "jobs", claim.Columns, "\"state\" = 1", IsUnique: false));
        claim.Columns.ShouldBe(["k", "state", "q", "due"]);
    }
}
