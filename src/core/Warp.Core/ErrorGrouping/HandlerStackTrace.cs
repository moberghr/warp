using System.Text;

namespace Warp.Core.ErrorGrouping;

/// <summary>
/// Ends a failed job's stored stack trace where Warp called the handler.
/// <para>
/// The trace is stored for every failed attempt, twice (the failure <c>JobLog</c> and the
/// <c>ErrorOccurrence</c>). Under the generated dispatch (8.0) an exception carries the whole chain below the
/// handler: the generated call (with the job type's full name), every pipeline behaviour and the worker's own
/// method, each with a file path. None of it says where the handler failed, and it made the stored text 2.5
/// times as long as 7.7's, whose reflection call ended the trace at the handler: 529 against 1,302 characters
/// on the retry benchmark, where it cost 6% more WAL per job than 7.7 with every other write smaller.
/// </para>
/// <para>
/// So the frames from the generated dispatcher down are dropped, per exception: an inner exception's frames
/// end at its own dispatcher frame and the text that follows it (the end-of-inner marker, the outer
/// exception's frames) is kept. A trace with no generated frame (a handler the generator did not see) is
/// returned as it is.
/// </para>
/// </summary>
internal static class HandlerStackTrace
{
    private const string DispatcherFrame = ".Generated.GeneratedJobDispatcher.";
    private const string Frame = "   at ";
    private const string AsyncSeparator = "--- End of stack trace from previous location ---";

    public static string? Trim(string? trace)
    {
        if (trace is null || !trace.Contains(DispatcherFrame, StringComparison.Ordinal))
        {
            return trace;
        }

        var result = new StringBuilder(trace.Length);
        var dropping = false;
        foreach (var segment in trace.AsSpan().EnumerateLines())
        {
            var isFrame = segment.StartsWith(Frame, StringComparison.Ordinal);
            if (dropping && (isFrame || segment.Trim().SequenceEqual(AsyncSeparator)))
            {
                continue;
            }

            dropping = isFrame && segment.Contains(DispatcherFrame, StringComparison.Ordinal);
            if (dropping)
            {
                continue;
            }

            if (result.Length > 0)
            {
                result.Append('\n');
            }

            result.Append(segment);
        }

        return result.ToString();
    }
}
