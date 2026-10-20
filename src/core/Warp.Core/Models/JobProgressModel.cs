namespace Warp.Core.Models;

/// <summary>
/// The current value of one progress bar a handler reported through <c>IJobContext.ReportProgress</c>.
/// </summary>
public class JobProgressModel
{
    /// <summary>
    /// The bar's name; empty for the unnamed <c>ReportProgress(int)</c> bar.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Percent complete, 0 to 100.
    /// </summary>
    public short Value { get; set; }

    /// <summary>
    /// When the worker persisted this value.
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}
