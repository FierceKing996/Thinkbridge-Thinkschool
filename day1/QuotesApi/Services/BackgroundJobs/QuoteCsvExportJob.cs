using System.Text;
using QuotesApi.Repositories;

namespace QuotesApi.Services.BackgroundJobs;

// The actual slow work Day 18 exists to move off the request thread: paging
// through every quote and writing a CSV. Small here, but stands in for
// anything that shouldn't block a request - a report, an export, a bulk
// notification fan-out.
public static class QuoteCsvExportJob
{
    public static BackgroundWorkItem Create(Guid jobId, string exportDirectory) =>
        async (services, ct) =>
        {
            var statusStore = services.GetRequiredService<ExportJobStatusStore>();
            var quotes = services.GetRequiredService<IQuoteRepository>();
            var logger = services.GetRequiredService<ILogger<QueuedHostedService>>();

            statusStore.Set(jobId, new ExportJobStatus(ExportJobState.Running));

            try
            {
                Directory.CreateDirectory(exportDirectory);
                var fileName = $"{jobId}.csv";
                var path = Path.Combine(exportDirectory, fileName);

                var csv = new StringBuilder("Id,Author,Text\n");
                const int pageSize = 200;
                var page = 1;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    var batch = await quotes.GetPagedAsync(page, pageSize, ct);
                    if (batch.Count == 0) break;

                    foreach (var quote in batch)
                    {
                        csv.Append(quote.Id).Append(',')
                            .Append(CsvEscape(quote.Author)).Append(',')
                            .Append(CsvEscape(quote.Text)).Append('\n');
                    }

                    if (batch.Count < pageSize) break;
                    page++;
                }

                await File.WriteAllTextAsync(path, csv.ToString(), Encoding.UTF8, ct);
                statusStore.Set(jobId, new ExportJobStatus(ExportJobState.Completed, FileName: fileName));
            }
            catch (OperationCanceledException)
            {
                // Shutdown mid-export: leave the job as "Running" rather than
                // "Failed" - it never got the chance to actually fail, the host
                // just asked it to stop. A future export attempt is the correct
                // recovery, not treating this as an error to alert on.
                logger.LogInformation("Quote export {JobId} cancelled by shutdown.", jobId);
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Quote export {JobId} failed.", jobId);
                statusStore.Set(jobId, new ExportJobStatus(ExportJobState.Failed, Error: ex.Message));
            }
        };

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
