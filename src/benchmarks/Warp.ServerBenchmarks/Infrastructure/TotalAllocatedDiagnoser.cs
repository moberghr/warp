using System.Collections.Concurrent;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;

namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// Custom BenchmarkDotNet diagnoser that tracks GC.GetTotalAllocatedBytes(precise: true)
/// across ALL threads (workers, background tasks), not just the benchmark thread.
/// </summary>
public class TotalAllocatedDiagnoser : IDiagnoser
{
    private readonly ConcurrentDictionary<BenchmarkCase, List<long>> _deltas = new();
    private long _beforeBytes;

    public IEnumerable<string> Ids => ["TotalAllocated"];

    public IEnumerable<IExporter> Exporters => [];

    public IEnumerable<IAnalyser> Analysers => [];

    public BenchmarkDotNet.Diagnosers.RunMode GetRunMode(BenchmarkCase benchmarkCase) => BenchmarkDotNet.Diagnosers.RunMode.NoOverhead;

    public bool RequiresBlockingAcknowledgments(BenchmarkCase benchmarkCase) => false;

    public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
    {
        switch (signal)
        {
            case HostSignal.BeforeActualRun:
                _beforeBytes = GC.GetTotalAllocatedBytes(true);
                break;

            case HostSignal.AfterActualRun:
                var afterBytes = GC.GetTotalAllocatedBytes(true);
                var delta = afterBytes - _beforeBytes;
                var list = _deltas.GetOrAdd(parameters.BenchmarkCase, _ => []);
                list.Add(delta);
                break;
        }
    }

    public IEnumerable<Metric> ProcessResults(DiagnoserResults results)
    {
        if (_deltas.TryGetValue(results.BenchmarkCase, out var deltas) && deltas.Count > 0)
        {
            var avgBytes = deltas.Average();
            yield return new Metric(TotalAllocatedMetricDescriptor.Instance, avgBytes);
        }
    }

    public void DisplayResults(ILogger logger)
    {
    }

    public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters) => [];

    private sealed class TotalAllocatedMetricDescriptor : IMetricDescriptor
    {
        public static readonly TotalAllocatedMetricDescriptor Instance = new();

        public string Id => "TotalAllocated";

        public string DisplayName => "Total Allocated (all threads)";

        public string Legend => "Total bytes allocated across all threads per iteration (GC.GetTotalAllocatedBytes)";

        public string NumberFormat => "0.##";

        public UnitType UnitType => UnitType.Size;

        public string Unit => "B";

        public bool TheGreaterTheBetter => false;

        public int PriorityInCategory => 0;

        public bool GetIsAvailable(Metric metric) => true;
    }
}

/// <summary>
/// BenchmarkDotNet config for component isolation benchmarks.
/// </summary>
public class ComponentBenchmarkConfig : ManualConfig
{
    public ComponentBenchmarkConfig()
    {
        AddDiagnoser(new MemoryDiagnoser(new MemoryDiagnoserConfig(false)));
        AddJob(Job.ShortRun);
    }
}

/// <summary>
/// BenchmarkDotNet config for full server benchmarks.
/// Adds TotalAllocatedDiagnoser alongside MemoryDiagnoser.
/// </summary>
public class ServerBenchmarkConfig : ManualConfig
{
    public ServerBenchmarkConfig()
    {
        AddDiagnoser(new MemoryDiagnoser(new MemoryDiagnoserConfig(false)));
        AddDiagnoser(new TotalAllocatedDiagnoser());
        AddDiagnoser(new PgStatStatementsDiagnoser());
        AddJob(Job.ShortRun
            .WithWarmupCount(1)
            .WithIterationCount(3));
    }
}
