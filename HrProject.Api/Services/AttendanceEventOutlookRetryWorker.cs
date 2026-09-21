namespace HrProject.Api.Services;

public sealed class AttendanceEventOutlookRetryWorker(
    AttendanceEventOutlookSyncService syncService,
    IConfiguration configuration,
    ILogger<AttendanceEventOutlookRetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!syncService.IsEnabled) return;
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("OutlookCalendar:AttendanceEventRetryIntervalSeconds", 60), 15, 3600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ids = await syncService.LoadPendingIds(100, stoppingToken);
                foreach (var id in ids)
                {
                    try { await syncService.SyncAsync(id, stoppingToken); }
                    catch (Exception exception) { logger.LogWarning(exception, "Attendance Event Outlook sync failed for queue {SyncId}", id); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Attendance Event Outlook retry cycle failed"); }
            await Task.Delay(interval, stoppingToken);
        }
    }
}
