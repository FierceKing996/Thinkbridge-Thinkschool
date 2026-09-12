using System.Collections.Concurrent;

namespace QuotesApi.Services.BackgroundJobs;

public enum ExportJobState { Queued, Running, Completed, Failed }

public record ExportJobStatus(ExportJobState State, string? FileName = null, string? Error = null);

// Deliberately in-memory, not a DB table: job status here is a UX convenience
// (poll for "is my export done yet") that resets on restart, not a durable
// record anything depends on - unlike the outbox rows in Day 20, losing this on
// a crash loses nothing that needs recovering.
public class ExportJobStatusStore
{
    private readonly ConcurrentDictionary<Guid, ExportJobStatus> _jobs = new();

    public void Set(Guid jobId, ExportJobStatus status) => _jobs[jobId] = status;

    public ExportJobStatus? Get(Guid jobId) => _jobs.GetValueOrDefault(jobId);
}
