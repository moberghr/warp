using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using Warp.Comparison;

// Warp against Hangfire: one arm per run, a summary of many. README.md describes the method.
//
//   dotnet run -c Release -- run --engine=warp-dispatcher|warp-single-worker|hangfire --conn="Host=..;Database=..;Username=..;Password=.."
//                                [--jobs=20000] [--servers=1] [--workers=10] [--job=noop|work] [--items=1000] [--out=result.json]
//   dotnet run -c Release -- summarize --results=<folder of result json files> [--out=summary.md]
var options = args
    .Skip(1)
    .Where(x => x.StartsWith("--", StringComparison.Ordinal))
    .Select(x => x[2..].Split('=', 2))
    .ToDictionary(x => x[0], x => x.Length > 1 ? x[1] : "true", StringComparer.Ordinal);
string Option(string name, string fallback) => options.GetValueOrDefault(name, fallback);
int Number(string name, int fallback) => int.Parse(Option(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

if (args.Length > 0 && string.Equals(args[0], "summarize", StringComparison.Ordinal))
{
    var results = Directory.GetFiles(Option("results", "."), "*.json", SearchOption.AllDirectories)
        .Select(x => JsonSerializer.Deserialize<RunResult>(File.ReadAllText(x), json)!)
        .ToList();
    var markdown = Summary.Render(results);
    await File.WriteAllTextAsync(Option("out", "summary.md"), markdown);
    Console.WriteLine(markdown);

    return 0;
}

var engine = Option("engine", "warp-dispatcher");
var connectionString = options.GetValueOrDefault("conn") ?? throw new ArgumentException("--conn is required", nameof(args));
var jobs = Number("jobs", 20_000);
var servers = Number("servers", 1);
var workers = Number("workers", 10);
var work = string.Equals(Option("job", "noop"), "work", StringComparison.Ordinal);
var items = Number("items", 1000);
var ct = CancellationToken.None;

var database = new NpgsqlConnectionStringBuilder(connectionString).Database ?? throw new ArgumentException("--conn must name a database", nameof(args));
var harness = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = Harness.Tag }.ConnectionString;
var monitor = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", ApplicationName = Harness.Tag }.ConnectionString;
Workload.ConnectionString = connectionString;

IArm Create() => engine switch
{
    "warp-dispatcher" => new WarpArm(connectionString, harness, servers, workers, dispatcher: true),
    "warp-single-worker" => new WarpArm(connectionString, harness, servers, workers, dispatcher: false),
    "hangfire" => new HangfireArm(connectionString, harness, servers, workers),
    _ => throw new NotSupportedException($"Unknown engine '{engine}'"),
};

async Task<long> WaitForAsync(IArm target, long completed)
{
    var deadline = DateTime.UtcNow.AddMinutes(30);
    long count;
    while ((count = await target.CountCompletedAsync(ct)) < completed)
    {
        if (DateTime.UtcNow > deadline)
        {
            throw new TimeoutException($"{engine} did not complete {completed} jobs in 30 minutes");
        }

        await Task.Delay(100, ct);
    }

    return count;
}

// Outside the window: the schema and the work table.
await using (var setup = Create())
{
    await setup.PrepareAsync(ct);
}

if (work)
{
    await using var connection = new NpgsqlConnection(harness);
    await connection.OpenAsync(ct);
    await using var create = new NpgsqlCommand(
        $"CREATE TABLE IF NOT EXISTS {Workload.ItemTable} (id int PRIMARY KEY, value bigint NOT NULL DEFAULT 0); "
        + $"INSERT INTO {Workload.ItemTable} (id) SELECT generate_series(1, {items}) ON CONFLICT DO NOTHING",
        connection);
    await create.ExecuteNonQueryAsync(ct);
}

// Warm-up, outside the window, on servers of its own: 200 jobs through the whole path, so the measured run does
// not pay for compiling code, building the EF model or the library's first-use setup. Both libraries get it.
long warmedUp;
await using (var warmUp = Create())
{
    await warmUp.PrepareAsync(ct);
    await warmUp.PreloadAsync(200, work, items, ct);
    await warmUp.StartAsync(ct);
    warmedUp = await WaitForAsync(warmUp, 200);
    await warmUp.StopAsync(ct);
}

await using var arm = Create();
await arm.PrepareAsync(ct);

// The backlog, written with no server running.
var preload = Stopwatch.StartNew();
await arm.PreloadAsync(jobs, work, items, ct);
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{engine}: preloaded {jobs:N0} jobs in {preload.Elapsed.TotalSeconds:0.0} s"));

// Every connection the preload opened is closed, so its backends publish their counters before the first reading.
NpgsqlConnection.ClearAllPools();
await Task.Delay(TimeSpan.FromSeconds(2), ct);
await DatabaseCounters.ResetAsync(monitor, ct);
var before = await DatabaseCounters.ReadAsync(monitor, database, ct);
Harness.ResetQueries();

// The window: from starting the servers to the last job completed and the servers stopped.
await using var sampler = new ConnectionSampler(monitor, database);
var process = Process.GetCurrentProcess();
var cpuBefore = process.TotalProcessorTime;
var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
var windowStart = DateTime.UtcNow;
var drain = Stopwatch.StartNew();
await arm.StartAsync(ct);
await WaitForAsync(arm, warmedUp + jobs);
drain.Stop();
await arm.StopAsync(ct);
process.Refresh();
var cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
var (peakConnections, idleConnections) = await sampler.StopAsync();

// The library's connections are closed so their backends publish what they counted.
NpgsqlConnection.ClearAllPools();
await Task.Delay(TimeSpan.FromSeconds(2), ct);
var after = await DatabaseCounters.ReadAsync(monitor, database, ct);

// The measured jobs' completions only: the warm-up's finished before the window started.
var times = (await arm.CompletionTimesAsync(ct))
    .Where(x => x >= windowStart.AddSeconds(-1))
    .Order()
    .ToList();
var firstToLast = (times[^1] - times[0]).TotalSeconds;
var p10 = times[(int)(times.Count * 0.1)];
var p90 = times[(int)(times.Count * 0.9)];

string postgresVersion;
await using (var connection = new NpgsqlConnection(monitor))
{
    await connection.OpenAsync(ct);
    await using var version = new NpgsqlCommand("SHOW server_version", connection);
    postgresVersion = (string)(await version.ExecuteScalarAsync(ct))!;
}

var result = new RunResult(
    engine,
    work ? "work" : "noop",
    jobs,
    servers,
    workers,
    drain.Elapsed.TotalSeconds,
    firstToLast,
    firstToLast > 0 ? jobs / firstToLast : 0,
    (p90 - p10).TotalSeconds > 0 ? jobs * 0.8 / (p90 - p10).TotalSeconds : 0,
    (after.Statements - before.Statements) / (double)jobs,
    (after.WalBytes - before.WalBytes) / (double)jobs,
    (after.DbMs - before.DbMs) / jobs,
    Math.Max(0, after.Commits - before.Commits - Harness.Queries) / (double)jobs,
    cpuMs / jobs,
    allocated / 1024.0 / jobs,
    peakConnections,
    idleConnections,
    postgresVersion,
    Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
    typeof(Hangfire.BackgroundJob).Assembly.GetName().Version?.ToString() ?? "?");

await File.WriteAllTextAsync(Option("out", "result.json"), JsonSerializer.Serialize(result, json));
Console.WriteLine(JsonSerializer.Serialize(result, json));

return 0;
