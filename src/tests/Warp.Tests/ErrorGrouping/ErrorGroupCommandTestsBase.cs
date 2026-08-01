using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.ErrorGrouping;

/// <summary>
/// Write side of error grouping / Issues (§8.29): <see cref="ErrorGroupCommandService{TContext}"/> flips a group's
/// operator status and stamps <c>StatusChangedAt</c> (so a later occurrence counts as a regression), returning
/// false for an unknown fingerprint. Both providers.
/// </summary>
[GenerateDatabaseTests]
public abstract class ErrorGroupCommandTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ErrorGroupCommandTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private ErrorGroupCommandService<TestContext> Service()
        => new(_fixture.CreateContext(), TimeProvider.System, NullLogger<ErrorGroupCommandService<TestContext>>.Instance);

    [TimedFact]
    public async Task SetStatus_PersistsStatusAndStamp_ReturnsTrue()
    {
        const string fingerprint = "fp-resolve";
        var ctx = _fixture.CreateContext();
        ctx.Set<ErrorGroup>().Add(new ErrorGroup
        {
            Fingerprint = fingerprint,
            Source = ErrorSource.Job,
            Kind = ErrorKind.Exception,
            ExceptionType = "System.NullReferenceException",
            Title = "boom",
            Culprit = "Acme.Orders.ProcessOrderRequest",
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            Count = 3,
            Status = ErrorGroupStatus.Unresolved,
        });
        await ctx.SaveChangesAsync(Ct);

        var result = await Service().SetStatus(fingerprint, ErrorGroupStatus.Resolved, Ct);

        result.ShouldBeTrue();
        var group = await _fixture.CreateContext().Set<ErrorGroup>()
            .Where(x => x.Fingerprint == fingerprint)
            .FirstOrDefaultAsync(Ct);
        group.ShouldNotBeNull();
        group!.Status.ShouldBe(ErrorGroupStatus.Resolved);
        group.StatusChangedAt.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task SetStatus_UnknownFingerprint_ReturnsFalse()
    {
        (await Service().SetStatus("nope", ErrorGroupStatus.Ignored, Ct)).ShouldBeFalse();
    }
}
