using Warp.Core;
using Warp.Core.BackgroundServices;
using Warp.Core.Retry;
using Warp.Core.Sagas;
using Warp.Provider.PostgreSql;
using Warp.Test.Shared;
using Warp.Test.Shared.Handlers.BackgroundServices;
using Warp.Test.Shared.Handlers.Sagas;
using Warp.Worker;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddServices(context.Configuration);
        services.AddWarpWorker<TestContext>(options =>
        {
            options.UsePostgreSql();
            options.WorkerCount = 10;
            options.PollingInterval = TimeSpan.FromSeconds(5);
            options.AddRetry(o => o.MaxRetries = 3);
            options.AddSagas();

            // Push finalize/enqueue notifications so the dashboard (running in TestApp)
            // sees this worker's job lifecycle events too. Without this, push would be
            // limited to the TestApp's own worker pool — TestWorker activity would only
            // appear on the dashboard via the 30s safety-net poll.
            options.UseDatabasePush();

            // Demo background services — same registrations as TestApp so running both
            // demonstrates per-server scope (TickCounterService runs on each host) vs
            // singleton scope (JobStatsLoggerService runs on only one host at a time —
            // cycle the host that holds the lease to watch failover).
            options.AddBackgroundService<TickCounterService>();
            options.AddBackgroundService<JobStatsLoggerService>();
        });
        services.AddSagaHandler<OrderSagaWorkflow>();
    })
    .Build();

await host.RunAsync();
