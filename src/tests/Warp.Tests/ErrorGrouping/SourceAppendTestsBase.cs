using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.ClientObservability;
using Warp.Core.Data.Entities;
using Warp.Core.Endpoints;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;

namespace Warp.Tests.ErrorGrouping;

/// <summary>
/// Batch 3b (§8.29): the three off-hot-path flushers (adapter, client, endpoint) append an
/// <see cref="ErrorOccurrence"/> inbox row alongside their existing call-log row when a record represents an
/// error signal. Driven through each flusher's <c>PersistBatchAsync</c> directly (no background loop, §4.8).
/// Both providers.
/// </summary>
[GenerateDatabaseTests]
public abstract class SourceAppendTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected SourceAppendTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Adapter_FailedRecord_AppendsErrorOccurrence()
    {
        var record = new AdapterCallRecord
        {
            AdapterName = "vendor",
            Operation = "GetOrders",
            Timestamp = DateTime.UtcNow,
            Outcome = AdapterCallOutcome.Failed,
            ExceptionType = "System.InvalidOperationException",
            ExceptionMessage = "boom",
            MachineName = "test-host",
        };

        await AdapterCallFlusher<TestContext>.PersistBatchAsync(
            _fixture.CreateContext(), [record], new AdapterRegistry(), new WarpConfiguration(), TimeProvider.System, Ct);

        var occurrence = await _fixture.CreateContext().Set<ErrorOccurrence>().SingleAsync(Ct);
        occurrence.Source.ShouldBe(ErrorSource.Adapter);
        occurrence.Culprit.ShouldBe("vendor.GetOrders");
    }

    [TimedFact]
    public async Task Adapter_SuccessRecord_AppendsNoErrorOccurrence()
    {
        var record = new AdapterCallRecord
        {
            AdapterName = "vendor",
            Operation = "GetOrders",
            Timestamp = DateTime.UtcNow,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "test-host",
        };

        await AdapterCallFlusher<TestContext>.PersistBatchAsync(
            _fixture.CreateContext(), [record], new AdapterRegistry(), new WarpConfiguration(), TimeProvider.System, Ct);

        (await _fixture.CreateContext().Set<ErrorOccurrence>().CountAsync(Ct)).ShouldBe(0);
    }

    [TimedFact]
    public async Task Client_ErrorRecord_AppendsErrorOccurrence()
    {
        var record = new ClientEventRecord
        {
            Application = "shop",
            Type = ClientEventType.Error,
            Name = "TypeError",
            Message = "boom",
            Stack = "at x",
            Url = "/checkout",
            Timestamp = DateTime.UtcNow,
        };

        await ClientEventFlusher<TestContext>.PersistBatchAsync(
            _fixture.CreateContext(), [record], Guard(), new WarpConfiguration(), TimeProvider.System, Ct);

        var occurrence = await _fixture.CreateContext().Set<ErrorOccurrence>().SingleAsync(Ct);
        occurrence.Source.ShouldBe(ErrorSource.Client);
        occurrence.Culprit.ShouldBe("/checkout");
    }

    [TimedFact]
    public async Task Client_NonErrorRecord_AppendsNoErrorOccurrence()
    {
        var record = new ClientEventRecord
        {
            Application = "shop",
            Type = ClientEventType.Log,
            Level = "warn",
            Timestamp = DateTime.UtcNow,
        };

        await ClientEventFlusher<TestContext>.PersistBatchAsync(
            _fixture.CreateContext(), [record], Guard(), new WarpConfiguration(), TimeProvider.System, Ct);

        (await _fixture.CreateContext().Set<ErrorOccurrence>().CountAsync(Ct)).ShouldBe(0);
    }

    [TimedFact]
    public async Task Endpoint_ServerError_AppendsExceptionOccurrence()
    {
        var record = EndpointRecord(AdapterCallOutcome.Failed, statusCode: 500);

        await EndpointCallFlusher<TestContext>.PersistBatchAsync(
            _fixture.CreateContext(), [record], new WarpConfiguration(), TimeProvider.System, Ct);

        var occurrence = await _fixture.CreateContext().Set<ErrorOccurrence>().SingleAsync(Ct);
        occurrence.Source.ShouldBe(ErrorSource.Endpoint);
        occurrence.Kind.ShouldBe(ErrorKind.Exception);
    }

    [TimedFact]
    public async Task Endpoint_ClientError_AppendsStatusCodeOccurrence_EvenWhenSuppressed()
    {
        // A 4xx under RecordCalls=FailuresOnly is a suppressed row (Outcome=Success), but must still group.
        var record = EndpointRecord(AdapterCallOutcome.Success, statusCode: 422, suppressLog: true);

        await EndpointCallFlusher<TestContext>.PersistBatchAsync(
            _fixture.CreateContext(), [record], new WarpConfiguration(), TimeProvider.System, Ct);

        var occurrence = await _fixture.CreateContext().Set<ErrorOccurrence>().SingleAsync(Ct);
        occurrence.Kind.ShouldBe(ErrorKind.StatusCode);
        occurrence.StatusCode.ShouldBe(422);
    }

    [TimedFact]
    public async Task Endpoint_Success_AppendsNoErrorOccurrence()
    {
        var record = EndpointRecord(AdapterCallOutcome.Success, statusCode: 200);

        await EndpointCallFlusher<TestContext>.PersistBatchAsync(
            _fixture.CreateContext(), [record], new WarpConfiguration(), TimeProvider.System, Ct);

        (await _fixture.CreateContext().Set<ErrorOccurrence>().CountAsync(Ct)).ShouldBe(0);
    }

    private static ClientEventCardinality Guard(int cap = 200) => new(cap, cap, cap);

    private static EndpointCallRecord EndpointRecord(AdapterCallOutcome outcome, int statusCode, bool suppressLog = false)
        => new()
        {
            Method = "GET",
            RouteTemplate = "/orders/{id}",
            Operation = "GetOrder",
            Timestamp = DateTime.UtcNow,
            DurationMs = 5,
            Outcome = outcome,
            StatusCode = statusCode,
            MachineName = "test-host",
            SuppressLog = suppressLog,
        };
}
