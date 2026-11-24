using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Warp.Core.Entities;
using Warp.Core.Enums;

namespace Warp.MatrixBench;

/// <summary>
/// What must be true after a round, whatever its timings: every row settled, every parent finalized, every
/// job run exactly as often as its policy allows, and no update lost.
/// </summary>
public static class Correctness
{
    private static readonly State[] LiveStates = [State.Enqueued, State.Awaiting, State.Processing, State.Scheduled];

    public static async Task<List<CorrectnessCheck>> CheckAsync(
        IHost host, BenchTarget target, ExecutionRecorder recorder, WorkloadPlan plan, bool timedOut)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MatrixContext>();
        context.Database.SetCommandTimeout(300);

        var groups = await context.Set<Job>()
            .AsNoTracking()
            .GroupBy(x => new { x.Kind, x.Type, x.CurrentState })
            .Select(x =>
                new
                {
                    x.Key.Kind,
                    x.Key.Type,
                    x.Key.CurrentState,
                    Count = x.Count(),
                })
            .ToListAsync();

        var rows = groups.ConvertAll(x => new RowGroup(x.Kind, x.Kind == JobKind.Job ? ShortName(x.Type) : x.Kind.ToString(), x.CurrentState, x.Count));

        var checks = new List<CorrectnessCheck>
        {
            new("drained", !timedOut, timedOut ? "did not drain within the timeout" : "drained"),
            CheckNoLiveRows(rows),
            CheckJobStates(rows, plan),
            CheckExecutions(recorder, plan),
        };

        if (plan.Messages > 0 || plan.Batches > 0)
        {
            checks.Add(CheckParents(rows, plan));
            checks.Add(await CheckRetriesAsync(context, recorder, plan));
        }

        if (plan.DbJobs > 0)
        {
            var sum = await BenchItemStore.SumAsync(target);
            checks.Add(new CorrectnessCheck(
                "db-sum",
                sum == plan.DbJobs,
                $"SUM(bench.item.value) = {sum:N0}, expected {plan.DbJobs:N0} (one increment per db job)"));
        }

        return checks;
    }

    private static CorrectnessCheck CheckNoLiveRows(List<RowGroup> rows)
    {
        // The recurring job is left out: its next occurrence may legitimately be enqueued at the moment of
        // the check. Its executions are still held to once per job id below.
        var live = rows
            .Where(x => LiveStates.Contains(x.State))
            .Where(x => !string.Equals(x.Type, nameof(RecurringBenchJob), StringComparison.Ordinal))
            .ToList();

        var processing = live
            .Where(x => x.State == State.Processing)
            .Sum(x => x.Count);

        var detail = live.Count == 0
            ? "every row terminal, none stuck Processing"
            : $"{live.Sum(x => x.Count):N0} live rows ({processing:N0} Processing): {Describe(live)}";

        return new CorrectnessCheck("no-live-rows", live.Count == 0, detail);
    }

    private static CorrectnessCheck CheckJobStates(List<RowGroup> rows, WorkloadPlan plan)
    {
        var problems = new List<string>();
        var jobRows = rows
            .Where(x => x.Kind == JobKind.Job)
            .Where(x => !string.Equals(x.Type, nameof(RecurringBenchJob), StringComparison.Ordinal))
            .ToList();

        foreach (var (type, (state, count)) in plan.JobRows)
        {
            var actual = jobRows
                .Where(x => string.Equals(x.Type, type, StringComparison.Ordinal))
                .ToList();
            var inState = actual
                .Where(x => x.State == state)
                .Sum(x => x.Count);
            var elsewhere = actual
                .Where(x => x.State != state)
                .ToList();

            if (inState != count || elsewhere.Count > 0)
            {
                problems.Add($"{type}: {inState:N0} {state} (expected {count:N0})"
                    + (elsewhere.Count > 0 ? $", also {Describe(elsewhere)}" : string.Empty));
            }
        }

        var unexpected = jobRows
            .Where(x => !plan.JobRows.ContainsKey(x.Type))
            .ToList();
        if (unexpected.Count > 0)
        {
            problems.Add($"unexpected job rows: {Describe(unexpected)}");
        }

        var detail = problems.Count == 0
            ? $"{plan.JobRows.Sum(x => x.Value.Count):N0} job rows in their expected final state"
            : string.Join("; ", problems);

        return new CorrectnessCheck("job-states", problems.Count == 0, detail);
    }

    private static CorrectnessCheck CheckParents(List<RowGroup> rows, WorkloadPlan plan)
    {
        var messages = rows
            .Where(x => x.Kind == JobKind.Message)
            .ToList();
        var batches = rows
            .Where(x => x.Kind == JobKind.Batch)
            .ToList();
        var messagesDone = messages
            .Where(x => x.State == State.Completed)
            .Sum(x => x.Count);
        var batchesDone = batches
            .Where(x => x.State == State.Completed)
            .Sum(x => x.Count);
        var continuations = rows
            .Where(x => string.Equals(x.Type, nameof(ContinuationChildJob), StringComparison.Ordinal))
            .Where(x => x.State == State.Completed)
            .Sum(x => x.Count);

        var pass = messagesDone == plan.Messages
            && messages.Sum(x => x.Count) == plan.Messages
            && batchesDone == plan.Batches
            && batches.Sum(x => x.Count) == plan.Batches
            && continuations == plan.Specials;

        var detail = $"messages {messagesDone}/{plan.Messages} Completed ({Describe(messages)}), "
            + $"batches {batchesDone}/{plan.Batches} Completed ({Describe(batches)}), "
            + $"continuations {continuations}/{plan.Specials} Completed";

        return new CorrectnessCheck("parents-finalized", pass, detail);
    }

    private static async Task<CorrectnessCheck> CheckRetriesAsync(MatrixContext context, ExecutionRecorder recorder, WorkloadPlan plan)
    {
        var attempts = recorder.Executions.Values
            .Where(x => string.Equals(x.Kind, ExecutionKinds.Failing, StringComparison.Ordinal))
            .GroupBy(x => x.Count)
            .OrderBy(x => x.Key)
            .Select(x => $"{x.Count()}x{x.Key}")
            .ToList();
        var allThree = recorder.Executions.Values
            .Where(x => string.Equals(x.Kind, ExecutionKinds.Failing, StringComparison.Ordinal))
            .Count(x => x.Count == ExecutionKinds.FailingAttempts);

        var failingType = typeof(FailingJob).AssemblyQualifiedName;
        var metadata = await context.Set<Job>()
            .AsNoTracking()
            .Where(x => x.Type == failingType)
            .Select(x => x.Metadata)
            .ToListAsync();
        var retried = metadata
            .Select(RetriedTimes)
            .GroupBy(x => x?.ToString() ?? "none")
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{x.Count()}x{x.Key}")
            .ToList();
        var metadataOk = metadata.Count == plan.Specials
            && metadata.TrueForAll(x => RetriedTimes(x) == ExecutionKinds.FailingAttempts - 1);

        var detail = $"attempts per failing job [{string.Join(", ", attempts)}], expected {plan.Specials}x{ExecutionKinds.FailingAttempts}; "
            + $"retried-times metadata [{string.Join(", ", retried)}], expected {plan.Specials}x{ExecutionKinds.FailingAttempts - 1}";

        return new CorrectnessCheck("retries", allThree == plan.Specials && metadataOk, detail);
    }

    private static CorrectnessCheck CheckExecutions(ExecutionRecorder recorder, WorkloadPlan plan)
    {
        var problems = new List<string>();
        var byKind = recorder.Executions.Values
            .GroupBy(x => x.Kind, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);

        foreach (var (kind, expected) in plan.ExecutionsByKind)
        {
            var executed = byKind.GetValueOrDefault(kind) ?? [];
            var over = executed.Count(x => x.Count > ExecutionKinds.MaxExecutions(kind));
            if (executed.Count != expected || over > 0)
            {
                problems.Add($"{kind}: {executed.Count:N0} ids ran (expected {expected:N0}), {over:N0} ran more than {ExecutionKinds.MaxExecutions(kind)}x");
            }
        }

        var recurringOver = (byKind.GetValueOrDefault(ExecutionKinds.Recurring) ?? []).Count(x => x.Count > 1);
        if (recurringOver > 0)
        {
            problems.Add($"recurring: {recurringOver} ids ran more than once");
        }

        var unknown = byKind.Keys
            .Where(x => !plan.ExecutionsByKind.ContainsKey(x))
            .Where(x => !string.Equals(x, ExecutionKinds.Recurring, StringComparison.Ordinal))
            .ToList();
        if (unknown.Count > 0)
        {
            problems.Add($"unexpected kinds ran: {string.Join(", ", unknown)}");
        }

        var total = recorder.Executions.Values.Sum(x => x.Count);
        var recurring = (byKind.GetValueOrDefault(ExecutionKinds.Recurring) ?? []).Count;

        var detail = problems.Count == 0
            ? $"{total:N0} handler executions, every job id within its limit (recurring ran {recurring}x)"
            : string.Join("; ", problems);

        return new CorrectnessCheck("executions", problems.Count == 0, detail);
    }

    /// <summary>The retried-times counter wherever the metadata keeps it: the key's spelling is the generator's business.</summary>
    private static int? RetriedTimes(string? metadata)
    {
        if (string.IsNullOrEmpty(metadata))
        {
            return null;
        }

        using var document = JsonDocument.Parse(metadata);

        return Find(document.RootElement);

        static int? Find(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Contains("retried", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Number)
                {
                    return property.Value.GetInt32();
                }

                if (Find(property.Value) is { } nested)
                {
                    return nested;
                }
            }

            return null;
        }
    }

    private static string ShortName(string? assemblyQualifiedName)
    {
        if (string.IsNullOrEmpty(assemblyQualifiedName))
        {
            return "(none)";
        }

        var typeName = assemblyQualifiedName.Split(',')[0];

        return typeName[(typeName.LastIndexOf('.') + 1)..];
    }

    private static string Describe(IEnumerable<RowGroup> rows) =>
        string.Join(", ", rows.Select(x => $"{x.Type}/{x.State}={x.Count:N0}"));

    private sealed record RowGroup(JobKind Kind, string Type, State State, int Count);
}
