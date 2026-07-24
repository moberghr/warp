using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.Endpoints;
using Warp.Core.Enums;
using Warp.Core.Observability;

namespace Warp.Tests.Observability;

/// <summary>
/// NoDb coverage for the OTel call-log recorders (<see cref="OtelAdapterCallRecorder"/> /
/// <see cref="OtelEndpointCallRecorder"/>): each completed record becomes ONE structured log carrying every
/// captured field as a log property (level-by-outcome), it writes no DB rows, and <c>Record</c> never throws.
/// </summary>
[Trait("Category", "NoDb")]
public class OtelRecorderTests
{
    [TimedFact]
    public void AdapterRecorder_Success_EmitsInformationLog_WithAllFields()
    {
        var (recorder, logs) = CreateAdapterRecorder(applicationName: "orders");

        var record = new AdapterCallRecord
        {
            AdapterName = "vendor",
            Operation = "GetOrders",
            GroupName = "shop-eu",
            Timestamp = DateTime.UtcNow,
            DurationMs = 42,
            Attempts = 1,
            Outcome = AdapterCallOutcome.Success,
            StatusCode = 200,
            RequestSummary = "GET /orders/{id}",
            RequestHeaders = "Authorization: ***",
            ResponseHeaders = "Content-Type: application/json",
            RequestBody = "{\"id\":42}",
            ResponseBody = "{\"ok\":true}",
            MachineName = "test-host",
            TraceId = "trace-abc",
            CorrelationId = "delivery-42",
            Tags = [new KeyValuePair<string, string>("region", "eu")],
        };

        recorder.Record(record).ShouldBeTrue();

        var log = logs.Logs.ShouldHaveSingleItem();
        log.Category.ShouldBe("Warp.Adapters.CallLog");
        log.Level.ShouldBe(LogLevel.Information);

        log.Fields["adapter"].ShouldBe("vendor");
        log.Fields["operation"].ShouldBe("GetOrders");
        log.Fields["outcome"].ShouldBe("success");
        log.Fields["group"].ShouldBe("shop-eu");
        log.Fields["status"].ShouldBe(200);
        log.Fields["durationMs"].ShouldBe(42.0);
        log.Fields["attempts"].ShouldBe(1);
        log.Fields["requestSummary"].ShouldBe("GET /orders/{id}");
        log.Fields["requestHeaders"].ShouldBe("Authorization: ***");
        log.Fields["responseHeaders"].ShouldBe("Content-Type: application/json");
        log.Fields["requestBody"].ShouldBe("{\"id\":42}");
        log.Fields["responseBody"].ShouldBe("{\"ok\":true}");
        log.Fields["machineName"].ShouldBe("test-host");
        log.Fields["traceId"].ShouldBe("trace-abc");
        log.Fields["correlationId"].ShouldBe("delivery-42");
        log.Fields["application"].ShouldBe("orders");
        log.Fields["tag.region"].ShouldBe("eu");
    }

    [TimedFact]
    public void AdapterRecorder_Failure_EmitsWarningLog_WithExceptionDetails()
    {
        var (recorder, logs) = CreateAdapterRecorder();

        var record = new AdapterCallRecord
        {
            AdapterName = "vendor",
            Operation = "GetOrders",
            Timestamp = DateTime.UtcNow,
            DurationMs = 5,
            Attempts = 2,
            Outcome = AdapterCallOutcome.Failed,
            ExceptionType = typeof(InvalidOperationException).FullName,
            ExceptionMessage = "boom",
            MachineName = "test-host",
        };

        recorder.Record(record).ShouldBeTrue();

        var log = logs.Logs.ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Warning);
        log.Fields["outcome"].ShouldBe("failed");
        log.Fields["exceptionType"].ShouldBe(typeof(InvalidOperationException).FullName);
        log.Fields["exceptionMessage"].ShouldBe("boom");
    }

    [TimedFact]
    public void AdapterRecorder_NoCapture_OmitsOptionalFields_ButKeepsRequired()
    {
        var (recorder, logs) = CreateAdapterRecorder();

        var record = new AdapterCallRecord
        {
            AdapterName = "vendor",
            Operation = "GetOrders",
            Timestamp = DateTime.UtcNow,
            DurationMs = 1,
            Attempts = 1,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "test-host",
        };

        recorder.Record(record).ShouldBeTrue();

        var log = logs.Logs.ShouldHaveSingleItem();
        log.Fields.ShouldContainKey("adapter");
        log.Fields.ShouldNotContainKey("requestBody");
        log.Fields.ShouldNotContainKey("responseBody");
        log.Fields.ShouldNotContainKey("application");
        log.Fields.ShouldNotContainKey("group");
    }

    [TimedFact]
    public void AdapterRecorder_ThrowingLoggerFactory_DoesNotThrow_ReturnsTrue()
    {
        // Record must never throw or fail a user call — a logger that throws is swallowed.
        var recorder = new OtelAdapterCallRecorder(new ThrowingLoggerFactory(), Options.Create(new WarpConfiguration()));

        var record = new AdapterCallRecord
        {
            AdapterName = "vendor",
            Operation = "GetOrders",
            Timestamp = DateTime.UtcNow,
            DurationMs = 1,
            Attempts = 1,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "test-host",
        };

        recorder.Record(record).ShouldBeTrue();
    }

    [TimedFact]
    public void EndpointRecorder_Success_EmitsInformationLog_WithAllFields()
    {
        var (recorder, logs) = CreateEndpointRecorder(applicationName: "orders");

        var record = new EndpointCallRecord
        {
            Method = "GET",
            RouteTemplate = "/orders/{id}",
            Operation = "GET /orders/{id}",
            GroupName = "web",
            Timestamp = DateTime.UtcNow,
            DurationMs = 12,
            Outcome = AdapterCallOutcome.Success,
            StatusCode = 200,
            RemoteIp = "203.0.113.4",
            UserAgent = "curl/8",
            User = "alice",
            RequestHeaders = "Accept: application/json",
            ResponseHeaders = "Content-Type: application/json",
            RequestBody = "{\"x\":1}",
            ResponseBody = "{\"ok\":true}",
            MachineName = "test-host",
            TraceId = Guid.NewGuid(),
            TagsJson = "{\"tenant\":\"acme\"}",
        };

        recorder.Record(record).ShouldBeTrue();

        var log = logs.Logs.ShouldHaveSingleItem();
        log.Category.ShouldBe("Warp.Endpoints.CallLog");
        log.Level.ShouldBe(LogLevel.Information);

        log.Fields["method"].ShouldBe("GET");
        log.Fields["route"].ShouldBe("/orders/{id}");
        log.Fields["operation"].ShouldBe("GET /orders/{id}");
        log.Fields["outcome"].ShouldBe("success");
        log.Fields["group"].ShouldBe("web");
        log.Fields["status"].ShouldBe(200);
        log.Fields["remoteIp"].ShouldBe("203.0.113.4");
        log.Fields["userAgent"].ShouldBe("curl/8");
        log.Fields["user"].ShouldBe("alice");
        log.Fields["requestBody"].ShouldBe("{\"x\":1}");
        log.Fields["responseBody"].ShouldBe("{\"ok\":true}");
        log.Fields["tags"].ShouldBe("{\"tenant\":\"acme\"}");
        log.Fields["application"].ShouldBe("orders");
    }

    [TimedFact]
    public void EndpointRecorder_Failed_EmitsWarningLog()
    {
        var (recorder, logs) = CreateEndpointRecorder();

        var record = new EndpointCallRecord
        {
            Method = "POST",
            RouteTemplate = "/orders",
            Operation = "POST /orders",
            Timestamp = DateTime.UtcNow,
            DurationMs = 3,
            Outcome = AdapterCallOutcome.Failed,
            StatusCode = 500,
            MachineName = "test-host",
        };

        recorder.Record(record).ShouldBeTrue();

        var log = logs.Logs.ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Warning);
        log.Fields["outcome"].ShouldBe("failed");
        log.Fields["status"].ShouldBe(500);
    }

    private static (OtelAdapterCallRecorder Recorder, CapturingLoggerProvider Logs) CreateAdapterRecorder(string? applicationName = null)
    {
        var logs = new CapturingLoggerProvider();
        var recorder = new OtelAdapterCallRecorder(new CapturingLoggerFactory(logs), Options.Create(new WarpConfiguration { ApplicationName = applicationName }));

        return (recorder, logs);
    }

    private static (OtelEndpointCallRecorder Recorder, CapturingLoggerProvider Logs) CreateEndpointRecorder(string? applicationName = null)
    {
        var logs = new CapturingLoggerProvider();
        var recorder = new OtelEndpointCallRecorder(new CapturingLoggerFactory(logs), Options.Create(new WarpConfiguration { ApplicationName = applicationName }));

        return (recorder, logs);
    }

    private sealed class ThrowingLoggerFactory : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new ThrowingLogger();

        public void Dispose()
        {
        }

        private sealed class ThrowingLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => throw new InvalidOperationException("logger boom");
        }
    }
}
