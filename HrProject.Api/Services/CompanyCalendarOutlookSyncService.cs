using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace HrProject.Api.Services;

public sealed class CompanyCalendarOutlookSyncService(
    NpgsqlDataSource dataSource,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration)
{
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? cachedAccessToken;
    private DateTimeOffset accessTokenExpiresAt;

    public bool IsEnabled =>
        configuration.GetValue("OutlookCalendar:Enabled", true) &&
        configuration.GetValue("OutlookCalendar:CompanyCalendarEnabled", true);

    public async Task<IReadOnlyList<long>> LoadPendingIds(int limit, CancellationToken cancellationToken)
    {
        if (!IsEnabled) return [];
        await BackfillActiveEmployees(cancellationToken);
        const string sql = """
            SELECT id
            FROM public.work_calendar_outlook_events
            WHERE sync_status IN ('PENDING', 'FAILED')
              AND (last_attempted_at IS NULL OR last_attempted_at <= CURRENT_TIMESTAMP - INTERVAL '1 minute')
            ORDER BY CASE WHEN desired_action = 'DELETE' THEN 0 ELSE 1 END,
                     COALESCE(last_attempted_at, created_at), id
            LIMIT @limit
            """;
        var result = new List<long>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetInt64(0));
        return result;
    }

    public async Task SyncAsync(long syncId, CancellationToken cancellationToken)
    {
        if (!IsEnabled) return;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var lockId = unchecked((int)(syncId % int.MaxValue));
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_lock(39039, @lock_id)", connection))
        {
            lockCommand.Parameters.AddWithValue("lock_id", lockId);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            var item = await LoadItem(connection, syncId, cancellationToken);
            if (item is null) return;

            if (item.DesiredAction == "DELETE")
            {
                await DeleteEvent(connection, item, cancellationToken);
                return;
            }

            if (!item.EmployeeEmail.Contains('@', StringComparison.Ordinal))
                throw new InvalidOperationException($"พนักงาน {item.EmployeeId} ไม่มี Email สำหรับ Outlook");

            if (item.OutlookEventId is not null &&
                !string.Equals(item.EventMailboxEmail, item.EmployeeEmail, StringComparison.OrdinalIgnoreCase))
            {
                await DeleteGraphEvent(item.EventMailboxEmail, item.OutlookEventId, cancellationToken);
                item = item with { OutlookEventId = null, OutlookWebLink = null, EventMailboxEmail = null };
            }

            if (item.OutlookEventId is null)
                await CreateEvent(connection, item, cancellationToken);
            else
                await UpdateEvent(connection, item, cancellationToken);
        }
        catch (Exception exception)
        {
            await MarkFailed(connection, syncId, SafeError(exception), cancellationToken);
            throw;
        }
        finally
        {
            await using var unlockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(39039, @lock_id)", connection);
            unlockCommand.Parameters.AddWithValue("lock_id", lockId);
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task BackfillActiveEmployees(CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.work_calendar_outlook_events
                (work_calendar_day_id, employee_id, employee_email,
                 calendar_date, day_type, event_name, event_note,
                 desired_action, sync_status)
            SELECT day.id, employee.employee_code, BTRIM(basic.email_address),
                   day.calendar_date, day.day_type, day.name, day.note,
                   'UPSERT', 'PENDING'
            FROM public.work_calendar_days day
            CROSS JOIN public.employees employee
            JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
            WHERE day.calendar_date >= CURRENT_DATE
              AND employee.is_active = TRUE
              AND NULLIF(BTRIM(basic.email_address), '') IS NOT NULL
              AND BTRIM(basic.email_address) LIKE '%@%'
            ON CONFLICT (calendar_date, employee_id) DO NOTHING
            """;
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task CreateEvent(
        NpgsqlConnection connection,
        SyncItem item,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessToken(cancellationToken);
        using var request = CreateGraphRequest(
            HttpMethod.Post,
            $"users/{Uri.EscapeDataString(item.EmployeeEmail)}/calendar/events",
            token);
        request.Content = JsonContent.Create(BuildEvent(item, item.TransactionId.ToString()));
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw GraphException("สร้างปฏิทินบริษัทใน Outlook", response.StatusCode, responseText);

        using var json = JsonDocument.Parse(responseText);
        var eventId = json.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Microsoft Graph ไม่ได้ส่ง Event ID กลับมา");
        var webLink = json.RootElement.TryGetProperty("webLink", out var property)
            ? property.GetString()
            : null;
        await MarkSucceeded(connection, item.Id, item.EmployeeEmail, eventId, webLink, cancellationToken);
    }

    private async Task UpdateEvent(
        NpgsqlConnection connection,
        SyncItem item,
        CancellationToken cancellationToken)
    {
        var mailbox = item.EventMailboxEmail ?? item.EmployeeEmail;
        var token = await GetAccessToken(cancellationToken);
        using var request = CreateGraphRequest(
            HttpMethod.Patch,
            $"users/{Uri.EscapeDataString(mailbox)}/events/{Uri.EscapeDataString(item.OutlookEventId!)}",
            token);
        request.Content = JsonContent.Create(BuildEvent(item, null));
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await ClearMissingEvent(connection, item.Id, cancellationToken);
            await CreateEvent(connection, item with
            {
                OutlookEventId = null,
                OutlookWebLink = null,
                EventMailboxEmail = null
            }, cancellationToken);
            return;
        }
        if (!response.IsSuccessStatusCode)
            throw GraphException("อัปเดตปฏิทินบริษัทใน Outlook", response.StatusCode, responseText);

        var webLink = item.OutlookWebLink;
        if (!string.IsNullOrWhiteSpace(responseText))
        {
            using var json = JsonDocument.Parse(responseText);
            if (json.RootElement.TryGetProperty("webLink", out var property))
                webLink = property.GetString();
        }
        await MarkSucceeded(connection, item.Id, mailbox, item.OutlookEventId, webLink, cancellationToken);
    }

    private async Task DeleteEvent(
        NpgsqlConnection connection,
        SyncItem item,
        CancellationToken cancellationToken)
    {
        if (item.OutlookEventId is not null)
            await DeleteGraphEvent(item.EventMailboxEmail ?? item.EmployeeEmail,
                item.OutlookEventId, cancellationToken);

        const string sql = """
            UPDATE public.work_calendar_outlook_events
            SET outlook_event_id = NULL, outlook_web_link = NULL,
                sync_status = 'DELETED', retry_count = 0,
                last_sync_error = NULL, last_attempted_at = CURRENT_TIMESTAMP,
                synced_at = CURRENT_TIMESTAMP, updated_at = CURRENT_TIMESTAMP
            WHERE id = @id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", item.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteGraphEvent(
        string? mailbox,
        string eventId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mailbox)) return;
        var token = await GetAccessToken(cancellationToken);
        using var request = CreateGraphRequest(
            HttpMethod.Delete,
            $"users/{Uri.EscapeDataString(mailbox)}/events/{Uri.EscapeDataString(eventId)}",
            token);
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            throw GraphException("ลบปฏิทินบริษัทใน Outlook", response.StatusCode, responseText);
        }
    }

    private Dictionary<string, object?> BuildEvent(SyncItem item, string? transactionId)
    {
        var graphDate = item.CalendarDate.ToString(
            "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var graphEndDate = item.CalendarDate.AddDays(1).ToString(
            "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var typeName = item.DayType == "PUBLIC_HOLIDAY"
            ? "วันหยุดนักขัตฤกษ์"
            : "วันเสาร์ทำงาน";
        var body = $"""
            <p><strong>ประเภท:</strong> {WebUtility.HtmlEncode(typeName)}</p>
            <p><strong>รายละเอียด:</strong> {WebUtility.HtmlEncode(item.EventName)}</p>
            {(string.IsNullOrWhiteSpace(item.EventNote) ? "" : $"<p><strong>หมายเหตุ:</strong> {WebUtility.HtmlEncode(item.EventNote)}</p>")}
            <p>รายการนี้สร้างโดยระบบ HR สำหรับพนักงานทุกคน</p>
            """;
        var calendarEvent = new Dictionary<string, object?>
        {
            ["subject"] = $"Test [{typeName}] {item.EventName}",
            ["body"] = new { contentType = "HTML", content = body },
            ["start"] = new { dateTime = $"{graphDate}T00:00:00", timeZone = TimeZone },
            ["end"] = new { dateTime = $"{graphEndDate}T00:00:00", timeZone = TimeZone },
            ["isAllDay"] = true,
            ["showAs"] = "free",
            ["sensitivity"] = "normal",
            ["isReminderOn"] = false
        };
        if (!string.IsNullOrWhiteSpace(transactionId))
            calendarEvent["transactionId"] = transactionId;
        return calendarEvent;
    }

    private string TimeZone => configuration["OutlookCalendar:TimeZone"] ?? "SE Asia Standard Time";

    private async Task<string> GetAccessToken(CancellationToken cancellationToken)
    {
        if (cachedAccessToken is not null && accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return cachedAccessToken;
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedAccessToken is not null && accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return cachedAccessToken;
            using var response = await httpClientFactory.CreateClient().PostAsync(
                $"https://login.microsoftonline.com/{Uri.EscapeDataString(Required("AzureAd:TenantId"))}/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = Required("AzureAd:ClientId"),
                    ["client_secret"] = Required("AzureAd:ClientSecret"),
                    ["scope"] = "https://graph.microsoft.com/.default",
                    ["grant_type"] = "client_credentials"
                }), cancellationToken);
            var jsonText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw GraphException("ขอ Access Token", response.StatusCode, jsonText);
            using var json = JsonDocument.Parse(jsonText);
            cachedAccessToken = json.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Microsoft Graph ไม่ได้ส่ง Access Token กลับมา");
            var expiresIn = json.RootElement.TryGetProperty("expires_in", out var property)
                ? property.GetInt32()
                : 3600;
            accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            return cachedAccessToken;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private static HttpRequestMessage CreateGraphRequest(HttpMethod method, string relativeUrl, string token)
    {
        var request = new HttpRequestMessage(method, $"https://graph.microsoft.com/v1.0/{relativeUrl}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation(
            "Prefer", "IdType=\"ImmutableId\", outlook.timezone=\"SE Asia Standard Time\"");
        return request;
    }

    private static async Task<SyncItem?> LoadItem(
        NpgsqlConnection connection,
        long id,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, employee_id, employee_email, event_mailbox_email,
                   calendar_date, day_type, event_name, event_note,
                   outlook_event_id, outlook_web_link, transaction_id, desired_action
            FROM public.work_calendar_outlook_events
            WHERE id = @id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SyncItem(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetFieldValue<DateOnly>(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetGuid(10), reader.GetString(11))
            : null;
    }

    private static async Task MarkSucceeded(
        NpgsqlConnection connection,
        long id,
        string mailbox,
        string? eventId,
        string? webLink,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE public.work_calendar_outlook_events
            SET event_mailbox_email = @mailbox,
                outlook_event_id = @event_id, outlook_web_link = @web_link,
                sync_status = 'SYNCED', retry_count = 0,
                last_sync_error = NULL, last_attempted_at = CURRENT_TIMESTAMP,
                synced_at = CURRENT_TIMESTAMP, updated_at = CURRENT_TIMESTAMP
            WHERE id = @id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("mailbox", mailbox);
        command.Parameters.Add(new NpgsqlParameter<string?>("event_id", eventId));
        command.Parameters.Add(new NpgsqlParameter<string?>("web_link", webLink));
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ClearMissingEvent(
        NpgsqlConnection connection,
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE public.work_calendar_outlook_events
            SET event_mailbox_email = NULL, outlook_event_id = NULL, outlook_web_link = NULL
            WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkFailed(
        NpgsqlConnection connection,
        long id,
        string error,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE public.work_calendar_outlook_events
            SET sync_status = 'FAILED', retry_count = retry_count + 1,
                last_sync_error = @error, last_attempted_at = CURRENT_TIMESTAMP,
                updated_at = CURRENT_TIMESTAMP
            WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("error", error);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string Required(string key) => configuration[key]
        ?? throw new InvalidOperationException($"{key} is not configured.");

    private static InvalidOperationException GraphException(
        string operation,
        HttpStatusCode statusCode,
        string response) =>
        new($"{operation} ไม่สำเร็จ ({(int)statusCode}): {Trim(response, 800)}");

    private static string SafeError(Exception exception) => Trim(exception.Message, 2000);
    private static string Trim(string value, int length) =>
        value.Length <= length ? value : value[..length];

    private sealed record SyncItem(
        long Id,
        string EmployeeId,
        string EmployeeEmail,
        string? EventMailboxEmail,
        DateOnly CalendarDate,
        string DayType,
        string EventName,
        string? EventNote,
        string? OutlookEventId,
        string? OutlookWebLink,
        Guid TransactionId,
        string DesiredAction);
}
