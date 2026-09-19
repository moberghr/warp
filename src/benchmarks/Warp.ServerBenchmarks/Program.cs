using BenchmarkDotNet.Running;
using Warp.Core.Concurrency;
using Warp.ServerBenchmarks.Benchmarks;
using Warp.ServerBenchmarks.Lab;

// One Warp server as a child process, launched by ClusterLab. Kept first so the coordinator's
// spawn path is unambiguous.
if (args.Length > 0 && string.Equals(args[0], "node", StringComparison.OrdinalIgnoreCase))
{
    var nodeConnection = string.Empty;
    var nodeWorkers = 20;
    var nodeDispatcher = false;

    for (var i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--connection=", StringComparison.OrdinalIgnoreCase))
        {
            nodeConnection = args[i]["--connection=".Length..];
        }
        else if (args[i].StartsWith("--workers=", StringComparison.OrdinalIgnoreCase))
        {
            nodeWorkers = int.Parse(args[i]["--workers=".Length..]);
        }
        else if (string.Equals(args[i], "--dispatcher", StringComparison.OrdinalIgnoreCase))
        {
            nodeDispatcher = true;
        }
    }

    await ClusterLab.RunNodeAsync(nodeConnection, nodeWorkers, nodeDispatcher);
}
else if (args.Length > 0 && string.Equals(args[0], "cluster", StringComparison.OrdinalIgnoreCase))
{
    var clusterJobs = 40_000;
    var clusterWorkers = 20;
    var clusterServers = 2;
    var clusterArrival = 0;
    var clusterRepeats = 3;
    var clusterDispatcher = false;
    var clusterIdle = 0;
    string? clusterConnection = null;
    var clusterHandlerMs = 0;

    for (var i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--jobs=", StringComparison.OrdinalIgnoreCase))
        {
            clusterJobs = int.Parse(args[i]["--jobs=".Length..]);
        }
        else if (args[i].StartsWith("--workers=", StringComparison.OrdinalIgnoreCase))
        {
            clusterWorkers = int.Parse(args[i]["--workers=".Length..]);
        }
        else if (args[i].StartsWith("--servers=", StringComparison.OrdinalIgnoreCase))
        {
            clusterServers = int.Parse(args[i]["--servers=".Length..]);
        }
        else if (args[i].StartsWith("--arrival=", StringComparison.OrdinalIgnoreCase))
        {
            clusterArrival = int.Parse(args[i]["--arrival=".Length..]);
        }
        else if (args[i].StartsWith("--repeats=", StringComparison.OrdinalIgnoreCase))
        {
            clusterRepeats = int.Parse(args[i]["--repeats=".Length..]);
        }
        else if (string.Equals(args[i], "--dispatcher", StringComparison.OrdinalIgnoreCase))
        {
            clusterDispatcher = true;
        }
        else if (args[i].StartsWith("--idle=", StringComparison.OrdinalIgnoreCase))
        {
            clusterIdle = int.Parse(args[i]["--idle=".Length..]);
        }
        else if (args[i].StartsWith("--connection=", StringComparison.OrdinalIgnoreCase))
        {
            clusterConnection = args[i]["--connection=".Length..];
        }
        else if (args[i].StartsWith("--handler-ms=", StringComparison.OrdinalIgnoreCase))
        {
            clusterHandlerMs = int.Parse(args[i]["--handler-ms=".Length..]);
        }
    }

    await ClusterLab.RunAsync(
        clusterJobs, clusterWorkers, clusterServers, clusterArrival, clusterRepeats, clusterDispatcher, clusterIdle, clusterConnection, clusterHandlerMs);
}
else if (args.Length > 0 && string.Equals(args[0], "stress", StringComparison.OrdinalIgnoreCase))
{
    var workers = 10;
    var jobsPerRound = 10_000;
    var rounds = 10;
    var useDispatcher = false;

    for (var i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--workers=", StringComparison.OrdinalIgnoreCase))
        {
            workers = int.Parse(args[i]["--workers=".Length..]);
        }
        else if (args[i].StartsWith("--jobs=", StringComparison.OrdinalIgnoreCase))
        {
            jobsPerRound = int.Parse(args[i]["--jobs=".Length..]);
        }
        else if (args[i].StartsWith("--rounds=", StringComparison.OrdinalIgnoreCase))
        {
            rounds = int.Parse(args[i]["--rounds=".Length..]);
        }
        else if (string.Equals(args[i], "--dispatcher", StringComparison.OrdinalIgnoreCase))
        {
            useDispatcher = true;
        }
    }

    await MemoryStressTest.RunAsync(workers, jobsPerRound, rounds, useDispatcher);
}
else if (args.Length > 0 && string.Equals(args[0], "idle", StringComparison.OrdinalIgnoreCase))
{
    var workers = 5;
    var settleSeconds = 45;
    var windowSeconds = 60;
    var track = "top";
    var servers = 1;

    for (var i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--workers=", StringComparison.OrdinalIgnoreCase))
        {
            workers = int.Parse(args[i]["--workers=".Length..]);
        }
        else if (args[i].StartsWith("--settle=", StringComparison.OrdinalIgnoreCase))
        {
            settleSeconds = int.Parse(args[i]["--settle=".Length..]);
        }
        else if (args[i].StartsWith("--window=", StringComparison.OrdinalIgnoreCase))
        {
            windowSeconds = int.Parse(args[i]["--window=".Length..]);
        }
        else if (args[i].StartsWith("--track=", StringComparison.OrdinalIgnoreCase))
        {
            track = args[i]["--track=".Length..];
        }
        else if (args[i].StartsWith("--servers=", StringComparison.OrdinalIgnoreCase))
        {
            servers = int.Parse(args[i]["--servers=".Length..]);
        }
    }

    await IdleServerProbe.RunAsync(workers, TimeSpan.FromSeconds(settleSeconds), TimeSpan.FromSeconds(windowSeconds), track, servers);
}
else if (args.Length > 0 && string.Equals(args[0], "rtt", StringComparison.OrdinalIgnoreCase))
{
    var rttSeconds = 5;
    string? rttConnection = null;

    for (var i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase))
        {
            rttSeconds = int.Parse(args[i]["--seconds=".Length..]);
        }
        else if (args[i].StartsWith("--connection=", StringComparison.OrdinalIgnoreCase))
        {
            rttConnection = args[i]["--connection=".Length..];
        }
    }

    await RoundTripProbe.RunAsync(rttSeconds, rttConnection);
}
else if (args.Length > 0 && string.Equals(args[0], "lockprobe", StringComparison.OrdinalIgnoreCase))
{
    var probeIterations = 2000;
    var probeMaxCount = 1;
    var probeSeparatePool = false;
    var probeDataSource = false;
    var probeConnection = string.Empty;

    for (var i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--iterations=", StringComparison.OrdinalIgnoreCase))
        {
            probeIterations = int.Parse(args[i]["--iterations=".Length..]);
        }
        else if (args[i].StartsWith("--max-count=", StringComparison.OrdinalIgnoreCase))
        {
            probeMaxCount = int.Parse(args[i]["--max-count=".Length..]);
        }
        else if (string.Equals(args[i], "--separate-pool", StringComparison.OrdinalIgnoreCase))
        {
            probeSeparatePool = true;
        }
        else if (string.Equals(args[i], "--data-source", StringComparison.OrdinalIgnoreCase))
        {
            probeDataSource = true;
        }
        else if (args[i].StartsWith("--connection=", StringComparison.OrdinalIgnoreCase))
        {
            probeConnection = args[i]["--connection=".Length..];
        }
    }

    await LockProbe.RunAsync(probeIterations, probeMaxCount, probeSeparatePool, probeDataSource, probeConnection);
}
else if (args.Length > 0 && string.Equals(args[0], "load", StringComparison.OrdinalIgnoreCase))
{
    var scenario = LoadScenario.Jobs;
    var jobs = 500_000;
    var workers = 20;
    var tabs = 10;
    var useDispatcher = false;
    int? prefetchCount = null;
    int? completionBatchSize = null;
    var payloadBytes = 0;
    var tune = "none";
    var repeats = 1;
    var types = 1;
    var arrival = 0;
    var sqlServer = false;
    var loadServers = 1;
    var idleSeconds = 120;
    string? connectionString = null;
    var keys = 1;
    var limit = 1;
    var mode = ConcurrencyMode.Wait;
    var handlerMs = 0;

    for (var i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--scenario=", StringComparison.OrdinalIgnoreCase))
        {
            scenario = Enum.Parse<LoadScenario>(args[i]["--scenario=".Length..], ignoreCase: true);
        }
        else if (args[i].StartsWith("--jobs=", StringComparison.OrdinalIgnoreCase))
        {
            jobs = int.Parse(args[i]["--jobs=".Length..]);
        }
        else if (args[i].StartsWith("--workers=", StringComparison.OrdinalIgnoreCase))
        {
            workers = int.Parse(args[i]["--workers=".Length..]);
        }
        else if (args[i].StartsWith("--tabs=", StringComparison.OrdinalIgnoreCase))
        {
            tabs = int.Parse(args[i]["--tabs=".Length..]);
        }
        else if (args[i].StartsWith("--idle=", StringComparison.OrdinalIgnoreCase))
        {
            idleSeconds = int.Parse(args[i]["--idle=".Length..]);
        }
        else if (args[i].StartsWith("--connection=", StringComparison.OrdinalIgnoreCase))
        {
            connectionString = args[i]["--connection=".Length..];
        }
        else if (string.Equals(args[i], "--dispatcher", StringComparison.OrdinalIgnoreCase))
        {
            useDispatcher = true;
        }
        else if (args[i].StartsWith("--prefetch=", StringComparison.OrdinalIgnoreCase))
        {
            prefetchCount = int.Parse(args[i]["--prefetch=".Length..]);
        }
        else if (args[i].StartsWith("--completion-batch=", StringComparison.OrdinalIgnoreCase))
        {
            completionBatchSize = int.Parse(args[i]["--completion-batch=".Length..]);
        }
        else if (args[i].StartsWith("--payload=", StringComparison.OrdinalIgnoreCase))
        {
            payloadBytes = int.Parse(args[i]["--payload=".Length..]);
        }
        else if (args[i].StartsWith("--tune=", StringComparison.OrdinalIgnoreCase))
        {
            tune = args[i]["--tune=".Length..];
        }
        else if (args[i].StartsWith("--repeats=", StringComparison.OrdinalIgnoreCase))
        {
            repeats = int.Parse(args[i]["--repeats=".Length..]);
        }
        else if (args[i].StartsWith("--types=", StringComparison.OrdinalIgnoreCase))
        {
            types = int.Parse(args[i]["--types=".Length..]);
        }
        else if (args[i].StartsWith("--arrival=", StringComparison.OrdinalIgnoreCase))
        {
            arrival = int.Parse(args[i]["--arrival=".Length..]);
        }
        else if (string.Equals(args[i], "--sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            sqlServer = true;
        }
        else if (args[i].StartsWith("--servers=", StringComparison.OrdinalIgnoreCase))
        {
            loadServers = int.Parse(args[i]["--servers=".Length..]);
        }
        else if (args[i].StartsWith("--keys=", StringComparison.OrdinalIgnoreCase))
        {
            keys = int.Parse(args[i]["--keys=".Length..]);
        }
        else if (args[i].StartsWith("--limit=", StringComparison.OrdinalIgnoreCase))
        {
            limit = int.Parse(args[i]["--limit=".Length..]);
        }
        else if (args[i].StartsWith("--mode=", StringComparison.OrdinalIgnoreCase))
        {
            mode = Enum.Parse<ConcurrencyMode>(args[i]["--mode=".Length..], ignoreCase: true);
        }
        else if (args[i].StartsWith("--handler-ms=", StringComparison.OrdinalIgnoreCase))
        {
            handlerMs = int.Parse(args[i]["--handler-ms=".Length..]);
        }
    }

    await LoadLab.RunAsync(
        scenario, jobs, workers, tabs, TimeSpan.FromSeconds(idleSeconds), connectionString, useDispatcher, prefetchCount, completionBatchSize, payloadBytes, tune, repeats, types, arrival, sqlServer, loadServers, keys, limit, mode, handlerMs);
}
else
{
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
