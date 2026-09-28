using Microsoft.AspNetCore.Mvc;
using QuotesApi.Services.BackgroundJobs;

namespace QuotesApi.Controllers;

// Day 18: moves the slow work (paging every quote, writing a CSV) off the
// request thread. POST returns immediately with a job id; the actual export
// runs on QueuedHostedService's drain loop.
[ApiController]
[Asp.Versioning.ApiVersion("1.0")]
[Route("api/exports/quotes")]
public class ExportsController(
    IBackgroundTaskQueue taskQueue,
    ExportJobStatusStore statusStore,
    IWebHostEnvironment env) : ControllerBase
{
    private string ExportDirectory => Path.Combine(env.ContentRootPath, "App_Data", "exports");

    // POST /api/exports/quotes
    [HttpPost]
    public async Task<IActionResult> StartExport(CancellationToken ct)
    {
        var jobId = Guid.NewGuid();
        statusStore.Set(jobId, new ExportJobStatus(ExportJobState.Queued));

        // Day 26: the request's own trace context, captured here and handed to the
        // job. Activity.Current does not flow across the queue on its own - by the
        // time QueuedHostedService dequeues this, the HTTP request's Activity has
        // long since ended, and it's running on an unrelated async flow with no
        // ambient Activity at all. Capturing the context now and starting the
        // job's Activity as its child (see QuoteCsvExportJob) is what makes the
        // resulting trace actually stitch API -> worker -> DB into one trace ID
        // instead of two unrelated ones.
        var triggeringContext = System.Diagnostics.Activity.Current?.Context ?? default;

        await taskQueue.QueueBackgroundWorkItemAsync(
            QuoteCsvExportJob.Create(jobId, ExportDirectory, triggeringContext), ct);

        return Accepted($"/api/exports/quotes/{jobId}", new { jobId });
    }

    // GET /api/exports/quotes/{jobId}
    [HttpGet("{jobId:guid}")]
    public IActionResult GetStatus(Guid jobId)
    {
        var status = statusStore.Get(jobId);
        return status is null ? NotFound() : Ok(status);
    }

    // GET /api/exports/quotes/{jobId}/download
    [HttpGet("{jobId:guid}/download")]
    public IActionResult Download(Guid jobId)
    {
        var status = statusStore.Get(jobId);
        if (status is not { State: ExportJobState.Completed, FileName: not null })
        {
            return NotFound();
        }

        var path = Path.Combine(ExportDirectory, status.FileName);
        if (!System.IO.File.Exists(path)) return NotFound();

        return PhysicalFile(Path.GetFullPath(path), "text/csv", status.FileName);
    }
}
