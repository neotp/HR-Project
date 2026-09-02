namespace HrProject.Api.Services;

public sealed class CompanyCalendarOutlookRetryWorker(
    CompanyCalendarOutlookSyncService syncService,
    IConfiguration configuration,
    ILogger<CompanyCalendarOutlookRetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!syncService.IsEnabled) return;
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("OutlookCalendar:CompanyCalendarRetryIntervalSeconds", 60),
            15, 3600));
        var batchSize = Math.Clamp(
            configuration.GetValue("OutlookCalendar:CompanyCalendarBatchSize", 200),
            1, 500);
        var parallelism = Math.Clamp(
            configuration.GetValue("OutlookCalendar:CompanyCalendarParallelism", 4),
            1, 10);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ids = await syncService.LoadPendingIds(batchSize, stoppingToken);
                await Parallel.ForEachAsync(ids,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = parallelism,
                        CancellationToken = stoppingToken
                    },
                    async (id, token) =>
                    {
                        try { await syncService.SyncAsync(id, token); }
                        catch (Exception exception)
                        {
                            logger.LogWarning(exception,
                                "Company calendar Outlook sync failed for queue {SyncId}", id);
                        }
                    });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Company calendar Outlook retry cycle failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
