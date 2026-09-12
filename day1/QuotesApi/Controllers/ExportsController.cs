using Microsoft.AspNetCore.Mvc;
using QuotesApi.Services.BackgroundJobs;

namespace QuotesApi.Controllers;

// Day 18: moves the slow work (paging every quote, writing a CSV) off the
// request thread. POST returns immediately with a job id; the actual export
// runs on QueuedHostedService's drain loop.
[ApiController]
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

        await taskQueue.QueueBackgroundWorkItemAsync(QuoteCsvExportJob.Create(jobId, ExportDirectory), ct);

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
