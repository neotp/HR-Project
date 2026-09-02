namespace HrProject.Api.Services;

public sealed class OutlookCalendarRetryWorker(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<OutlookCalendarRetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("OutlookCalendar:Enabled", true)) return;
        var interval = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("OutlookCalendar:RetryIntervalMinutes", 5), 1, 60));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<OutlookCalendarSyncService>();
                var ids = await service.LoadRetryDocumentIds(50, stoppingToken);
                foreach (var id in ids)
                {
                    try { await service.SyncAsync(id, stoppingToken); }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Retry Outlook calendar sync failed for leave document {DocumentId}", id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Outlook calendar retry cycle failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}

