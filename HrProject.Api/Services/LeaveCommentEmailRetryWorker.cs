namespace HrProject.Api.Services;

public sealed class LeaveCommentEmailRetryWorker(
    LeaveCommentEmailService service,
    ILogger<LeaveCommentEmailRetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var id in await service.LoadPendingCommentIds(100, stoppingToken))
                {
                    try { await service.SendAsync(id, stoppingToken); }
                    catch (Exception exception)
                    { logger.LogWarning(exception,"Leave comment email retry failed for comment {CommentId}",id); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception,"Leave comment email retry cycle failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1),stoppingToken);
        }
    }
}
