namespace Warp.Core.Services;

/// <summary>
/// Thrown by <see cref="IDeployVerdictService.GetVerdict"/> when <see cref="DeployVerdictQuery.Board"/> names no metric
/// board (matched ignoring case). The verdict route answers 404 <c>{ error }</c>: a CI step that names a board expects its
/// tiles to be judged, and silently judging none would read as a pass.
/// </summary>
public sealed class MetricBoardNotFoundException : Exception
{
    public MetricBoardNotFoundException()
    {
    }

    public MetricBoardNotFoundException(string message)
        : base(message)
    {
    }

    public MetricBoardNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
