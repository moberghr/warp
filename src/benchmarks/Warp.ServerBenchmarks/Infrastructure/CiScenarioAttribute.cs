namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// What a benchmark is for, in the words the CI report shows beside its numbers.
/// <para>
/// Kept on the benchmark rather than in the comparer so the explanation cannot drift from the code it
/// explains, and so a benchmark added without one is visible in the report as exactly that.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CiScenarioAttribute : Attribute
{
    public CiScenarioAttribute(string title, string measures, string why)
    {
        Title = title;
        Measures = measures;
        Why = why;
    }

    /// <summary>A short name for the scenario, used as its heading.</summary>
    public string Title { get; }

    /// <summary>What one operation does, in plain terms: the workload, not the implementation.</summary>
    public string Measures { get; }

    /// <summary>Why a change in these numbers would matter.</summary>
    public string Why { get; }

    /// <summary>Ordering in the report; lower first.</summary>
    public int Order { get; set; } = 100;
}

/// <summary>
/// A readable name for one value of a <c>[Params]</c> property, so the report says
/// "8 keys, contended" rather than <c>Keys: 8</c>. Parameters with a single value are left out of the
/// case name entirely; they are the same on every row and belong in the scenario description.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CaseLabelAttribute : Attribute
{
    public CaseLabelAttribute(string parameter, string value, string label)
    {
        Parameter = parameter;
        Value = value;
        Label = label;
    }

    public string Parameter { get; }

    /// <summary>The value as BenchmarkDotNet prints it in the case name: <c>8</c>, <c>True</c>, <c>SqlServer</c>.</summary>
    public string Value { get; }

    public string Label { get; }
}
