namespace HrProject.Api.Services;

public sealed class LeaveQuotaAnnualRolloverWorker(
    LeaveQuotaAnnualRolloverService service,
    IConfiguration configuration,
    ILogger<LeaveQuotaAnnualRolloverWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("LeaveQuotaAnnualRollover:Enabled", true))
            return;

        int? completedYear = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = GetBangkokNow();
                if (now.Month == 1 && now.Day == 1 &&
                    now.TimeOfDay >= new TimeSpan(0, 1, 0) && completedYear != now.Year)
                {
                    var processed = await service.ProcessAsync(now.Year, stoppingToken);
                    completedYear = now.Year;
                    if (processed > 0)
                        logger.LogInformation(
                            "Annual leave quota rollover completed for {QuotaYear}: {ProcessedRows} employee/type rows",
                            now.Year, processed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Annual leave quota rollover cycle failed");
            }

            // A one-minute tick makes a continuously running service execute at
            // 00:01 Bangkok time. Once successful, the in-memory guard and the
            // database unique key prevent repeated annual processing.
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private static DateTime GetBangkokNow()
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
    }
}
