namespace Warp.Core.Models;

public class DashboardStatistics
{
    public int Total { get; set; }

    public int Pending { get; set; }

    public int Scheduled { get; set; }

    public int Created { get; set; }

    public int Failed { get; set; }

    public int Completed { get; set; }

    public int Processing { get; set; }

    public int Servers { get; set; }

    public int Awaiting { get; set; }

    public int Deleted { get; set; }

    public int Messages { get; set; }

    public long TotalSucceeded { get; set; }

    public long TotalFailed { get; set; }

    public long TotalDeleted { get; set; }

    public long TotalCreated { get; set; }

    /// <summary>Records dropped by the outbound adapter recording pipeline in the last 24h (§8.19); a health signal.</summary>
    public long AdapterRecordsDropped { get; set; }

    /// <summary>Records dropped by the inbound endpoint recording pipeline in the last 24h (§8.21).</summary>
    public long EndpointRecordsDropped { get; set; }

    /// <summary>Client (browser) events dropped by the ingest pipeline in the last 24h (§8.27).</summary>
    public long ClientRecordsDropped { get; set; }

    public int Batches { get; set; }

    public int BatchesProcessing { get; set; }

    public int BatchesCompleted { get; set; }

    public int BatchesFailed { get; set; }

    public int BatchesAwaiting { get; set; }

    public int BatchesDeleted { get; set; }

    public int MessagesEnqueued { get; set; }

    public int MessagesProcessing { get; set; }

    public int MessagesCompleted { get; set; }

    public int MessagesFailed { get; set; }

    public string? DatabaseConnection { get; set; }
}
