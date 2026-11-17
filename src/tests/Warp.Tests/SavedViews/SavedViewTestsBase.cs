using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.SavedViews;
using Warp.Core.Services;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.Observability;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.SavedViews;

/// <summary>
/// Database coverage for saved views (§8.34): CRUD with the author and the structured audit (never the query), visibility
/// (the author's personal views, everyone's shared views), who may edit and delete (403 for someone else's personal
/// view or for a non-author changing the shared flag; anyone on a shared view), 404 for a deleted view, the per-author
/// and shared caps, the routes with two principals (author from the principal, never the body; <c>anonymous</c> on an
/// open dashboard; validation 400s) and a dashboard-only host.
/// </summary>
[GenerateDatabaseTests]
public abstract class SavedViewTestsBase : IAsyncLifetime
{
    private const string UserHeader = "x-test-user";

    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private readonly IDatabaseFixture _fixture;

    protected SavedViewTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Upsert_Insert_StoresTheActorAsAuthorAndAuditsWithoutTheQuery()
    {
        var logs = new CapturingLoggerProvider();
        var view = View("Checkout errors by version", query: "breakdown=%7Bversion%7D&filter=outcome%3Aerror");
        view.Author = "mallory";

        var result = await Command(logs).Upsert(view, "alice", Ct);

        result.Status.ShouldBe(SavedViewWriteStatus.Saved);
        var row = await _fixture.CreateContext().Set<SavedView>().SingleAsync(Ct);
        row.Id.ShouldBe(result.Id);
        row.Author.ShouldBe("alice");
        row.Page.ShouldBe(SavedViewValidation.MetricPage);
        row.Target.ShouldBe("shop.checkout.completed");
        row.Query.ShouldBe("breakdown=%7Bversion%7D&filter=outcome%3Aerror");
        row.CreatedAt.ShouldBe(Now);
        row.UpdatedAt.ShouldBe(Now);
        var audit = logs.Logs.ShouldHaveSingleItem();
        audit.Level.ShouldBe(LogLevel.Information);
        audit.Message.ShouldBe($"Saved view {result.Id} created by alice on metric");
        audit.Message.ShouldNotContain("outcome");
    }

    [TimedFact]
    public async Task Upsert_InsertWithACallerKey_IsStoreGenerated()
    {
        var view = View("Mine");
        view.Id = -5;

        var result = await Command().Upsert(view, "alice", Ct);

        result.Status.ShouldBe(SavedViewWriteStatus.Saved);
        result.Id.ShouldBeGreaterThan(0);
    }

    [TimedFact]
    public async Task Upsert_UpdateBySomeoneElseOnASharedView_KeepsTheAuthorAndAuditsTheEditor()
    {
        var id = await SeedViewAsync("Checkout", "alice", shared: true);
        var logs = new CapturingLoggerProvider();
        var edit = View("Checkout (7d)", query: "range=7d", shared: true);
        edit.Id = id;

        var result = await Command(logs).Upsert(edit, "bob", Ct);

        result.ShouldSatisfyAllConditions(x => x.Status.ShouldBe(SavedViewWriteStatus.Saved), x => x.Id.ShouldBe(id));
        var row = await _fixture.CreateContext().Set<SavedView>().SingleAsync(Ct);
        row.Name.ShouldBe("Checkout (7d)");
        row.Query.ShouldBe("range=7d");
        row.Author.ShouldBe("alice");
        logs.Logs.ShouldHaveSingleItem().Message.ShouldBe($"Saved view {id} updated by bob on metric");
    }

    [TimedFact]
    public async Task Upsert_UpdateOfAnotherUsersPersonalView_IsForbiddenAndChangesNothing()
    {
        var id = await SeedViewAsync("Mine", "alice", shared: false);
        var edit = View("Hijacked");
        edit.Id = id;

        var result = await Command().Upsert(edit, "bob", Ct);

        result.Status.ShouldBe(SavedViewWriteStatus.Forbidden);
        (await _fixture.CreateContext().Set<SavedView>().SingleAsync(Ct)).Name.ShouldBe("Mine");
    }

    [TimedTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Upsert_NonAuthorChangesTheSharedFlag_IsForbidden(bool stored, bool requested)
    {
        // Unsharing would hide another user's view from everyone; sharing someone's personal view would publish it.
        var id = await SeedViewAsync("Checkout", "alice", shared: stored);
        var edit = View("Checkout", shared: requested);
        edit.Id = id;

        var result = await Command().Upsert(edit, "bob", Ct);

        result.Status.ShouldBe(SavedViewWriteStatus.Forbidden);
        (await _fixture.CreateContext().Set<SavedView>().SingleAsync(Ct)).Shared.ShouldBe(stored);
    }

    [TimedFact]
    public async Task Upsert_AuthorUnsharesTheirView_IsSaved()
    {
        var id = await SeedViewAsync("Checkout", "alice", shared: true);
        var edit = View("Checkout", shared: false);
        edit.Id = id;

        var result = await Command().Upsert(edit, "alice", Ct);

        result.Status.ShouldBe(SavedViewWriteStatus.Saved);
        (await _fixture.CreateContext().Set<SavedView>().SingleAsync(Ct)).Shared.ShouldBeFalse();
    }

    [TimedFact]
    public async Task Upsert_UpdateOfADeletedView_ReturnsNotFoundAndRecreatesNothing()
    {
        var edit = View("Gone");
        edit.Id = 4242;

        var result = await Command().Upsert(edit, "alice", Ct);

        result.Status.ShouldBe(SavedViewWriteStatus.NotFound);
        (await _fixture.CreateContext().Set<SavedView>().CountAsync(Ct)).ShouldBe(0);
    }

    [TimedFact]
    public async Task Upsert_AuthorAtTheCap_ReturnsAuthorFullWhileAnotherAuthorCanStillSave()
    {
        await SeedManyAsync("alice", shared: false, SavedViewValidation.MaxViewsPerAuthor);

        var alice = await Command().Upsert(View("One more"), "alice", Ct);
        var bob = await Command().Upsert(View("Bob's"), "bob", Ct);

        alice.Status.ShouldBe(SavedViewWriteStatus.AuthorFull);
        bob.Status.ShouldBe(SavedViewWriteStatus.Saved);
    }

    [TimedFact]
    public async Task Upsert_AtTheSharedCap_RefusesASharedInsertAndASwitchToSharedButNotAPersonalView()
    {
        // The shared views are spread over three authors so no author cap is reached first.
        await SeedManyAsync("a", shared: true, 200);
        await SeedManyAsync("b", shared: true, 200);
        await SeedManyAsync("c", shared: true, SavedViewValidation.MaxSharedViews - 400);
        var personal = await SeedViewAsync("Mine", "alice", shared: false);
        var share = View("Mine", shared: true);
        share.Id = personal;

        var insert = await Command().Upsert(View("Shared", shared: true), "alice", Ct);
        var switched = await Command().Upsert(share, "alice", Ct);
        var personalInsert = await Command().Upsert(View("Mine too"), "alice", Ct);

        insert.Status.ShouldBe(SavedViewWriteStatus.SharedFull);
        switched.Status.ShouldBe(SavedViewWriteStatus.SharedFull);
        personalInsert.Status.ShouldBe(SavedViewWriteStatus.Saved);
    }

    [TimedFact]
    public async Task Delete_PersonalView_OnlyByItsAuthor()
    {
        var id = await SeedViewAsync("Mine", "alice", shared: false);
        var logs = new CapturingLoggerProvider();

        var byBob = await Command().Delete(id, "bob", Ct);
        var byAlice = await Command(logs).Delete(id, "alice", Ct);
        var again = await Command().Delete(id, "alice", Ct);

        byBob.ShouldBe(SavedViewDeleteStatus.Forbidden);
        byAlice.ShouldBe(SavedViewDeleteStatus.Deleted);
        again.ShouldBe(SavedViewDeleteStatus.NotFound);
        logs.Logs.ShouldHaveSingleItem().Message.ShouldBe($"Saved view {id} deleted by alice on metric");
    }

    [TimedFact]
    public async Task Delete_SharedView_ByAnyone()
    {
        var id = await SeedViewAsync("Checkout", "alice", shared: true);

        var status = await Command().Delete(id, "bob", Ct);

        status.ShouldBe(SavedViewDeleteStatus.Deleted);
        (await _fixture.CreateContext().Set<SavedView>().CountAsync(Ct)).ShouldBe(0);
    }

    [TimedFact]
    public async Task Delete_WhileTheAuthorUnsharesTheView_WaitsForTheLockAndThenRefuses()
    {
        // N=2: alice's unshare holds warp:saved-view on a second connection and has already made the view personal, pinned
        // inside its transaction by a BarrierSignal. Bob's delete must wait at the lock and re-read the row there; read
        // outside it, the view was still shared and bob deleted alice's now-personal view.
        var id = await SeedViewAsync("Checkout", "alice", shared: true);
        var barrier = new BarrierSignal();
        var holderContext = _fixture.CreateContext();
        var holder = TestTasks.QueriesFor(holderContext).RunUnderTransactionLockAsync(
            holderContext,
            TransactionLock.SavedViewKey,
            async (x, ct) =>
            {
                var view = await x.Set<SavedView>().Where(y => y.Id == id).SingleAsync(ct);
                view.Shared = false;
                await x.SaveChangesAsync(ct);
                barrier.Running.Release();
                await barrier.CanFinish.WaitAsync(ct);

                return 0;
            },
            Ct);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();

        var delete = Command().Delete(id, "bob", Ct);

        // Held at the lock: it does not finish while the unshare is inside.
        await Should.ThrowAsync<TimeoutException>(() => delete.WaitAsync(TimeSpan.FromMilliseconds(500), Ct));
        barrier.CanFinish.Release();
        (await holder).LockHeld.ShouldBeTrue();
        (await delete).ShouldBe(SavedViewDeleteStatus.Forbidden);
        var row = await _fixture.CreateContext().Set<SavedView>().SingleAsync(Ct);
        row.Shared.ShouldBeFalse();
    }

    [TimedFact]
    public async Task Delete_WhileTheLockIsHeldPastTheTimeout_ThrowsTimeoutAndDeletesNothing()
    {
        var id = await SeedViewAsync("Checkout", "alice", shared: true);
        var barrier = new BarrierSignal();
        var holder = HoldTheLockAsync(barrier);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
        try
        {
            await Should.ThrowAsync<TimeoutException>(() => Command(lockTimeout: TimeSpan.FromMilliseconds(300)).Delete(id, "bob", Ct));
        }
        finally
        {
            barrier.CanFinish.Release();
            await holder;
        }

        (await _fixture.CreateContext().Set<SavedView>().CountAsync(Ct)).ShouldBe(1);
    }

    [TimedFact]
    public async Task DeleteRoute_LockHeldPastTheTimeout_ReturnsServiceUnavailableWithAnError()
    {
        var id = await SeedViewAsync("Checkout", "alice", shared: true);
        await using var host = await CreateDashboardHostAsync(TimeSpan.FromMilliseconds(300));
        var barrier = new BarrierSignal();
        var holder = HoldTheLockAsync(barrier);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
        HttpResponseMessage response;
        try
        {
            response = await host.DeleteAsync("bob", id);
        }
        finally
        {
            barrier.CanFinish.Release();
            await holder;
        }

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ErrorAsync(response)).ShouldNotBeNullOrEmpty();
        (await _fixture.CreateContext().Set<SavedView>().CountAsync(Ct)).ShouldBe(1);
    }

    [TimedFact]
    public async Task GetViews_ReturnsTheCallersOwnAndEverySharedViewNewestFirst()
    {
        await SeedViewAsync("Alice personal", "alice", shared: false, createdAt: Now.AddMinutes(-3));
        await SeedViewAsync("Alice shared", "alice", shared: true, createdAt: Now.AddMinutes(-2));
        await SeedViewAsync("Bob personal", "bob", shared: false, createdAt: Now.AddMinutes(-1));

        var alice = await Query().GetViews("alice", null, null, Ct);
        var bob = await Query().GetViews("bob", null, null, Ct);

        alice.Select(x => x.Name).ShouldBe(["Alice shared", "Alice personal"]);
        bob.Select(x => x.Name).ShouldBe(["Bob personal", "Alice shared"]);
    }

    [TimedFact]
    public async Task GetViews_AuthorDifferingOnlyByCase_IsAnotherUser()
    {
        // A case-insensitive collation (SQL Server) must not list "Alice"'s personal views to "alice".
        await SeedViewAsync("Upper", "Alice", shared: false);

        (await Query().GetViews("alice", null, null, Ct)).ShouldBeEmpty();
    }

    [TimedFact]
    public async Task GetViews_FiltersByPageAndTarget()
    {
        await SeedViewAsync("Checkout", "alice", shared: true);
        await SeedViewAsync("Payments", "alice", shared: true, target: "payment.results");
        await SeedViewAsync("Unresolved", "alice", shared: true, page: SavedViewValidation.IssuesPage, target: null);

        var metric = await Query().GetViews("alice", SavedViewValidation.MetricPage, null, Ct);
        var checkout = await Query().GetViews("alice", SavedViewValidation.MetricPage, "shop.checkout.completed", Ct);
        var issues = await Query().GetViews("alice", SavedViewValidation.IssuesPage, null, Ct);

        metric.Select(x => x.Name).ShouldBe(["Payments", "Checkout"], ignoreOrder: true);
        checkout.ShouldHaveSingleItem().Name.ShouldBe("Checkout");
        issues.ShouldHaveSingleItem().Name.ShouldBe("Unresolved");
    }

    [TimedFact]
    public async Task Routes_TwoUsers_SeeAndChangeOnlyWhatTheyMay()
    {
        await using var host = await CreateDashboardHostAsync();

        var personal = await PostAsync(host, "alice", new { id = 0, name = "Alice personal", page = "metric", target = "c", query = "range=7d", shared = false });
        var shared = await PostAsync(host, "alice", new { id = 0, name = "Alice shared", page = "issues", query = "status=resolved", shared = true });
        var personalId = await IdAsync(personal);
        var sharedId = await IdAsync(shared);
        var asBob = await host.ListAsync("bob", string.Empty);
        var asAlice = await host.ListAsync("alice", string.Empty);
        var bobEditsPersonal = await PostAsync(host, "bob", new { id = personalId, name = "Hijacked", page = "metric", target = "c", query = string.Empty, shared = false });
        var bobDeletesPersonal = await host.DeleteAsync("bob", personalId);
        var bobEditsShared = await PostAsync(host, "bob", new { id = sharedId, name = "Resolved issues", page = "issues", query = "status=resolved", shared = true });
        var bobDeletesShared = await host.DeleteAsync("bob", sharedId);
        var bobDeletesAgain = await host.DeleteAsync("bob", sharedId);
        var bobEditsDeleted = await PostAsync(host, "bob", new { id = sharedId, name = "Back", page = "issues", query = string.Empty, shared = true });

        personal.StatusCode.ShouldBe(HttpStatusCode.OK);
        shared.StatusCode.ShouldBe(HttpStatusCode.OK);
        asBob.GetProperty("user").GetString().ShouldBe("bob");
        Names(asBob).ShouldBe(["Alice shared"]);
        asAlice.GetProperty("user").GetString().ShouldBe("alice");
        Names(asAlice).ShouldBe(["Alice shared", "Alice personal"]);
        bobEditsPersonal.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorAsync(bobEditsPersonal)).ShouldNotBeNullOrEmpty();
        bobDeletesPersonal.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorAsync(bobDeletesPersonal)).ShouldNotBeNullOrEmpty();
        bobEditsShared.StatusCode.ShouldBe(HttpStatusCode.OK);
        bobDeletesShared.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        bobDeletesAgain.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorAsync(bobDeletesAgain)).ShouldNotBeNullOrEmpty();
        bobEditsDeleted.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorAsync(bobEditsDeleted)).ShouldNotBeNullOrEmpty();
        var remaining = await _fixture.CreateContext().Set<SavedView>().SingleAsync(Ct);
        remaining.Name.ShouldBe("Alice personal");
        remaining.Author.ShouldBe("alice");
    }

    [TimedFact]
    public async Task PostRoute_AuthorInTheBody_IsIgnored()
    {
        await using var host = await CreateDashboardHostAsync();

        var response = await PostAsync(host, "carol", new { id = 0, name = "Mine", page = "jobs", target = "failed", query = "type=Orders.Charge", shared = false, author = "mallory" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _fixture.CreateContext().Set<SavedView>().SingleAsync(Ct)).Author.ShouldBe("carol");
    }

    [TimedFact]
    public async Task Routes_OpenDashboard_AuthorIsAnonymousAndTheListSaysSo()
    {
        await using var host = await CreateDashboardHostAsync();

        var response = await PostAsync(host, null, new { id = 0, name = "Everyone's", page = "jobs-by-type", target = "Orders.Charge", query = "metrics=handler", shared = true });
        var list = await host.ListAsync(null, "?page=jobs-by-type&target=Orders.Charge");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _fixture.CreateContext().Set<SavedView>().SingleAsync(Ct)).Author.ShouldBe(MetricAnnotationValidation.AnonymousAuthor);
        list.GetProperty("user").GetString().ShouldBe(MetricAnnotationValidation.AnonymousAuthor);
        Names(list).ShouldBe(["Everyone's"]);
    }

    [TimedTheory]
    [InlineData("unknown-key", "Query parameter 'utm_source' is not read by the issues page.")]
    [InlineData("bad-page", "Page must be one of: metric, issues, jobs, jobs-by-type.")]
    [InlineData("missing-target", "A metric view needs a target: the instrument name.")]
    [InlineData("blank-name", "Name is required and must be at most 200 characters.")]
    public async Task PostRoute_Invalid_ReturnsBadRequestWithTheMessageAndStoresNothing(string scenario, string expected)
    {
        await using var host = await CreateDashboardHostAsync();
        object body = scenario switch
        {
            "unknown-key" => new { id = 0, name = "x", page = "issues", query = "status=resolved&utm_source=mail", shared = true },
            "bad-page" => new { id = 0, name = "x", page = "webhooks", query = string.Empty, shared = true },
            "missing-target" => new { id = 0, name = "x", page = "metric", query = "range=7d", shared = true },
            _ => new { id = 0, name = "  ", page = "issues", query = string.Empty, shared = true },
        };

        var response = await PostAsync(host, "alice", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorAsync(response)).ShouldStartWith(expected);
        (await _fixture.CreateContext().Set<SavedView>().CountAsync(Ct)).ShouldBe(0);
    }

    [TimedFact]
    public async Task PostRoute_AtTheAuthorCap_ReturnsBadRequestWithAnError()
    {
        await SeedManyAsync("alice", shared: false, SavedViewValidation.MaxViewsPerAuthor);
        await using var host = await CreateDashboardHostAsync();

        var response = await PostAsync(host, "alice", new { id = 0, name = "One more", page = "issues", query = string.Empty, shared = false });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorAsync(response)).ShouldNotBeNullOrEmpty();
    }

    [TimedFact]
    public async Task GetRoute_UnknownPage_ReturnsBadRequest()
    {
        await using var host = await CreateDashboardHostAsync();

        using var response = await host.Client.GetAsync("/warp/api/views?page=webhooks", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TimedFact]
    public async Task DashboardOnlyHost_AddWarpAlone_SavesAndListsViews()
    {
        // No server, no AddMetrics: the saved-view services come from AddWarp, and the command takes the provider's lock.
        await using var probe = _fixture.CreateContext();
        var connectionString = probe.Database.GetConnectionString()!;
        var isPostgres = probe.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Services.AddDbContext<TestContext>(x =>
        {
            if (isPostgres)
            {
                x.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
            }
            else
            {
                x.UseSqlServer(connectionString);
            }
        });
        builder.Services.AddWarp<TestContext>(_ => { });
        await using var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);
        await app.StartAsync(Ct);
        try
        {
            using var client = app.GetTestClient();

            var post = await client.PostAsJsonAsync("/warp/api/views", new { id = 0, name = "Failed charges", page = "jobs", target = "failed", query = "type=Orders.Charge", shared = true }, Ct);
            var list = await client.GetFromJsonAsync<JsonElement>("/warp/api/views?page=jobs", Ct);

            post.StatusCode.ShouldBe(HttpStatusCode.OK);
            var view = list.GetProperty("views").EnumerateArray().ShouldHaveSingleItem();
            view.GetProperty("name").GetString().ShouldBe("Failed charges");
            view.GetProperty("target").GetString().ShouldBe("failed");
            view.GetProperty("query").GetString().ShouldBe("type=Orders.Charge");
            view.GetProperty("author").GetString().ShouldBe(MetricAnnotationValidation.AnonymousAuthor);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static SavedView View(string name, string query = "range=24h", bool shared = false)
    {
        var view = new SavedView
        {
            Name = name,
            Page = SavedViewValidation.MetricPage,
            Target = "shop.checkout.completed",
            Query = query,
            Shared = shared,
        };
        SavedViewValidation.TryValidate(view, out var error).ShouldBeTrue(error);

        return view;
    }

    private static List<string?> Names(JsonElement list)
        => [.. list.GetProperty("views").EnumerateArray().Select(x => x.GetProperty("name").GetString())];

    private static async Task<int> IdAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString();

    private static Task<HttpResponseMessage> PostAsync(DashboardHost host, string? user, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/warp/api/views") { Content = JsonContent.Create(body) };
        if (user is not null)
        {
            request.Headers.Add(UserHeader, user);
        }

        return host.Client.SendAsync(request, Ct);
    }

    private SavedViewQueryService<TestContext> Query() => new(_fixture.CreateContext());

    private SavedViewCommandService<TestContext> Command(CapturingLoggerProvider? logs = null, TimeSpan? lockTimeout = null)
    {
        var context = _fixture.CreateContext();
        var logger = logs is null
            ? NullLogger<SavedViewCommandService<TestContext>>.Instance
            : new CapturingLoggerFactory(logs).CreateLogger<SavedViewCommandService<TestContext>>();

        return new SavedViewCommandService<TestContext>(context, new FakeTimeProvider(new DateTimeOffset(Now)), logger, TestTasks.QueriesFor(context))
        {
            LockTimeout = lockTimeout ?? TransactionLock.CommandTimeout,
        };
    }

    // Holds warp:saved-view on a second connection (as another user's write would) until the barrier lets it finish.
    private Task<(bool LockHeld, int Result)> HoldTheLockAsync(BarrierSignal barrier)
    {
        var context = _fixture.CreateContext();

        return TestTasks.QueriesFor(context).RunUnderTransactionLockAsync(
            context,
            TransactionLock.SavedViewKey,
            async (_, ct) =>
            {
                barrier.Running.Release();
                await barrier.CanFinish.WaitAsync(ct);

                return 0;
            },
            Ct);
    }

    private async Task<int> SeedViewAsync(
        string name,
        string author,
        bool shared,
        string page = SavedViewValidation.MetricPage,
        string? target = "shop.checkout.completed",
        DateTime? createdAt = null)
    {
        var context = _fixture.CreateContext();
        var view = new SavedView
        {
            Name = name,
            Page = page,
            Target = target,
            Query = string.Empty,
            Shared = shared,
            Author = author,
            CreatedAt = createdAt ?? Now,
            UpdatedAt = createdAt ?? Now,
        };
        context.Set<SavedView>().Add(view);
        await context.SaveChangesAsync(Ct);

        return view.Id;
    }

    private async Task SeedManyAsync(string author, bool shared, int count)
    {
        var context = _fixture.CreateContext();
        context.Set<SavedView>().AddRange(Enumerable.Range(0, count).Select(x =>
            new SavedView
            {
                Name = $"{author} {x}",
                Page = SavedViewValidation.IssuesPage,
                Query = string.Empty,
                Shared = shared,
                Author = author,
                CreatedAt = Now,
                UpdatedAt = Now,
            }));
        await context.SaveChangesAsync(Ct);
    }

    private async Task<DashboardHost> CreateDashboardHostAsync(TimeSpan? lockTimeout = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);
        builder.Services.AddScoped<ISavedViewQueryService>(_ => Query());
        builder.Services.AddScoped<ISavedViewCommandService>(_ => Command(lockTimeout: lockTimeout));

        var app = builder.Build();

        // Stands in for whichever gate the host put in front of the dashboard: an authenticated principal with a name,
        // chosen per request so one host serves two users; no header = an open dashboard (no identity).
        app.Use((context, next) =>
        {
            var user = context.Request.Headers[UserHeader].ToString();
            if (user.Length > 0)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test"));
            }

            return next(context);
        });

        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);
        await app.StartAsync(CancellationToken.None);

        return new DashboardHost(app, app.GetTestClient());
    }

    private sealed record DashboardHost(WebApplication App, HttpClient Client) : IAsyncDisposable
    {
        public async Task<JsonElement> ListAsync(string? user, string query)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/warp/api/views{query}");
            if (user is not null)
            {
                request.Headers.Add(UserHeader, user);
            }

            using var response = await Client.SendAsync(request, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }

        public async Task<HttpResponseMessage> DeleteAsync(string? user, int id)
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, $"/warp/api/views/{id}");
            if (user is not null)
            {
                request.Headers.Add(UserHeader, user);
            }

            return await Client.SendAsync(request, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(CancellationToken.None);
            await App.DisposeAsync();
        }
    }
}
