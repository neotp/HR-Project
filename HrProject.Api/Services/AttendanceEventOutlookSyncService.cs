using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace HrProject.Api.Services;

public sealed class AttendanceEventOutlookSyncService(
    NpgsqlDataSource dataSource,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration)
{
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? cachedAccessToken;
    private DateTimeOffset accessTokenExpiresAt;

    public bool IsEnabled => configuration.GetValue("OutlookCalendar:Enabled", true) &&
        configuration.GetValue("OutlookCalendar:AttendanceEventEnabled", true);

    public async Task SyncBySourceEventId(long sourceEventId, CancellationToken token)
    {
        if (!IsEnabled) return;
        await using var command = dataSource.CreateCommand(
            "SELECT id FROM public.attendance_event_outlook_sync WHERE source_event_id=@source_id");
        command.Parameters.AddWithValue("source_id", sourceEventId);
        var id = (long?)await command.ExecuteScalarAsync(token);
        if (id.HasValue) await SyncAsync(id.Value, token);
    }

    public async Task<IReadOnlyList<long>> LoadPendingIds(int limit, CancellationToken token)
    {
        if (!IsEnabled) return [];
        const string refreshSql = """
            UPDATE public.attendance_event_outlook_sync sync
            SET employee_email = NULLIF(BTRIM(basic.email_address), ''), updated_at=CURRENT_TIMESTAMP
            FROM public.employees employee
            JOIN public.employee_basic_info basic ON basic.employee_id=employee.id
            WHERE employee.employee_code=sync.employee_id
              AND sync.sync_status IN ('PENDING','FAILED')
            """;
        await using (var refresh = dataSource.CreateCommand(refreshSql))
        {
            await refresh.ExecuteNonQueryAsync(token);
        }

        const string sql = """
            SELECT id FROM public.attendance_event_outlook_sync
            WHERE sync_status IN ('PENDING','FAILED')
              AND (last_attempted_at IS NULL OR last_attempted_at <= CURRENT_TIMESTAMP-INTERVAL '1 minute')
            ORDER BY CASE WHEN desired_action='DELETE' THEN 0 ELSE 1 END,
                     COALESCE(last_attempted_at,created_at),id
            LIMIT @limit
            """;
        var result = new List<long>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(reader.GetInt64(0));
        return result;
    }

    public async Task SyncAsync(long id, CancellationToken token)
    {
        if (!IsEnabled) return;
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(75075,@id)", connection))
        {
            command.Parameters.AddWithValue("id", unchecked((int)(id % int.MaxValue)));
            await command.ExecuteNonQueryAsync(token);
        }
        try
        {
            var item = await Load(connection, id, token);
            if (item is null) return;
            if (item.DesiredAction == "DELETE")
            {
                await Delete(connection, item, token);
                return;
            }
            if (string.IsNullOrWhiteSpace(item.EmployeeEmail) || !item.EmployeeEmail.Contains('@'))
                throw new InvalidOperationException(
                    $"Employee {item.EmployeeId} does not have an Outlook email address.");

            if (item.OutlookEventId is not null &&
                !string.Equals(item.EventMailboxEmail, item.EmployeeEmail, StringComparison.OrdinalIgnoreCase))
            {
                await DeleteGraph(item.EventMailboxEmail, item.OutlookEventId, token);
                item = item with { OutlookEventId = null, OutlookWebLink = null, EventMailboxEmail = null };
            }
            if (item.OutlookEventId is null) await Create(connection, item, token);
            else await Update(connection, item, token);
        }
        catch (Exception exception)
        {
            await MarkFailed(connection, id, Trim(exception.Message, 2000), token);
            throw;
        }
        finally
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(75075,@id)", connection);
            command.Parameters.AddWithValue("id", unchecked((int)(id % int.MaxValue)));
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task Create(NpgsqlConnection connection, Item item, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post,
            $"users/{Uri.EscapeDataString(item.EmployeeEmail!)}/calendar/events", await AccessToken(token));
        request.Content = JsonContent.Create(Payload(item, item.TransactionId.ToString()));
        using var response = await httpClientFactory.CreateClient().SendAsync(request, token);
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw GraphError("Create attendance event", response.StatusCode, text);
        using var json = JsonDocument.Parse(text);
        var eventId = json.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Microsoft Graph did not return an event ID.");
        var link = json.RootElement.TryGetProperty("webLink", out var value) ? value.GetString() : null;
        await MarkSucceeded(connection, item.Id, item.EmployeeEmail!, eventId, link, "SYNCED", token);
    }

    private async Task Update(NpgsqlConnection connection, Item item, CancellationToken token)
    {
        var mailbox = item.EventMailboxEmail ?? item.EmployeeEmail!;
        using var request = Request(HttpMethod.Patch,
            $"users/{Uri.EscapeDataString(mailbox)}/events/{Uri.EscapeDataString(item.OutlookEventId!)}",
            await AccessToken(token));
        request.Content = JsonContent.Create(Payload(item, null));
        using var response = await httpClientFactory.CreateClient().SendAsync(request, token);
        var text = await response.Content.ReadAsStringAsync(token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await MarkSucceeded(connection, item.Id, mailbox, null, null, "PENDING", token);
            await Create(connection,
                item with { OutlookEventId = null, OutlookWebLink = null, EventMailboxEmail = null }, token);
            return;
        }
        if (!response.IsSuccessStatusCode)
            throw GraphError("Update attendance event", response.StatusCode, text);
        var link = item.OutlookWebLink;
        if (!string.IsNullOrWhiteSpace(text))
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("webLink", out var value)) link = value.GetString();
        }
        await MarkSucceeded(connection, item.Id, mailbox, item.OutlookEventId, link, "SYNCED", token);
    }

    private async Task Delete(NpgsqlConnection connection, Item item, CancellationToken token)
    {
        if (item.OutlookEventId is not null)
            await DeleteGraph(item.EventMailboxEmail ?? item.EmployeeEmail, item.OutlookEventId, token);
        await MarkSucceeded(connection, item.Id, item.EventMailboxEmail ?? item.EmployeeEmail,
            null, null, "DELETED", token);
    }

    private async Task DeleteGraph(string? mailbox, string eventId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(mailbox)) return;
        using var request = Request(HttpMethod.Delete,
            $"users/{Uri.EscapeDataString(mailbox)}/events/{Uri.EscapeDataString(eventId)}",
            await AccessToken(token));
        using var response = await httpClientFactory.CreateClient().SendAsync(request, token);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            throw GraphError("Delete attendance event", response.StatusCode,
                await response.Content.ReadAsStringAsync(token));
    }

    private Dictionary<string, object?> Payload(Item item, string? transactionId)
    {
        var date = item.EventDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var pending = item.ReviewStatus == "PENDING_REVIEW";
        var subjectTitle = string.IsNullOrWhiteSpace(item.EventTitle) ? item.EventTypeName : item.EventTitle;
        var body = $"<p><strong>ประเภท:</strong> {WebUtility.HtmlEncode(item.EventTypeName)}</p>" +
                   $"<p><strong>รายละเอียด:</strong> {WebUtility.HtmlEncode(item.EventDetails ?? "-")}</p>";
        var result = new Dictionary<string, object?>
        {
            ["subject"] = $"{(pending ? "[รอตรวจสอบ] " : "")}{subjectTitle}",
            ["body"] = new { contentType = "HTML", content = body },
            ["start"] = new { dateTime = $"{date}T{item.StartTime:HH:mm:ss}", timeZone = TimeZone },
            ["end"] = new { dateTime = $"{date}T{item.EndTime:HH:mm:ss}", timeZone = TimeZone },
            ["showAs"] = item.EventType == "WORK_FROM_HOME" ? "workingElsewhere" : "busy",
            ["sensitivity"] = "private",
            ["isReminderOn"] = false
        };
        if (!string.IsNullOrWhiteSpace(transactionId)) result["transactionId"] = transactionId;
        return result;
    }

    private string TimeZone => configuration["OutlookCalendar:TimeZone"] ?? "SE Asia Standard Time";

    private async Task<string> AccessToken(CancellationToken token)
    {
        if (cachedAccessToken is not null && accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return cachedAccessToken;
        await tokenLock.WaitAsync(token);
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
                }), token);
            var text = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw GraphError("Request access token", response.StatusCode, text);
            using var json = JsonDocument.Parse(text);
            cachedAccessToken = json.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Microsoft Graph did not return an access token.");
            accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                json.RootElement.TryGetProperty("expires_in", out var value) ? value.GetInt32() : 3600);
            return cachedAccessToken;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, $"https://graph.microsoft.com/v1.0/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation(
            "Prefer", "IdType=\"ImmutableId\", outlook.timezone=\"SE Asia Standard Time\"");
        return request;
    }

    private static async Task<Item?> Load(NpgsqlConnection connection, long id, CancellationToken token)
    {
        const string sql = """
            SELECT id,employee_id,employee_email,event_mailbox_email,event_date,start_time,end_time,
                   event_type,event_type_name,event_title,event_details,review_status,outlook_event_id,
                   outlook_web_link,transaction_id,desired_action
            FROM public.attendance_event_outlook_sync WHERE id=@id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new Item(reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetFieldValue<DateOnly>(4), reader.GetFieldValue<TimeOnly>(5),
                reader.GetFieldValue<TimeOnly>(6), reader.GetString(7), reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13), reader.GetGuid(14), reader.GetString(15))
            : null;
    }

    private static async Task MarkSucceeded(
        NpgsqlConnection connection, long id, string? mailbox, string? eventId, string? link,
        string status, CancellationToken token)
    {
        const string sql = """
            UPDATE public.attendance_event_outlook_sync
            SET event_mailbox_email=@mailbox,outlook_event_id=@event_id,outlook_web_link=@link,
                sync_status=@status,retry_count=0,last_sync_error=NULL,
                last_attempted_at=CURRENT_TIMESTAMP,synced_at=CURRENT_TIMESTAMP,
                updated_at=CURRENT_TIMESTAMP WHERE id=@id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.Add(new NpgsqlParameter<string?>("mailbox", mailbox));
        command.Parameters.Add(new NpgsqlParameter<string?>("event_id", eventId));
        command.Parameters.Add(new NpgsqlParameter<string?>("link", link));
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task MarkFailed(
        NpgsqlConnection connection, long id, string error, CancellationToken token)
    {
        const string sql = """
            UPDATE public.attendance_event_outlook_sync
            SET sync_status='FAILED',retry_count=retry_count+1,last_sync_error=@error,
                last_attempted_at=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP WHERE id=@id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("error", error);
        await command.ExecuteNonQueryAsync(token);
    }

    private string Required(string key) => configuration[key]
        ?? throw new InvalidOperationException($"{key} is not configured.");
    private static InvalidOperationException GraphError(string action, HttpStatusCode status, string text) =>
        new($"{action} failed ({(int)status}): {Trim(text, 800)}");
    private static string Trim(string value, int length) => value.Length <= length ? value : value[..length];

    private sealed record Item(
        long Id, string EmployeeId, string? EmployeeEmail, string? EventMailboxEmail,
        DateOnly EventDate, TimeOnly StartTime, TimeOnly EndTime, string EventType,
        string EventTypeName, string? EventTitle, string? EventDetails, string ReviewStatus,
        string? OutlookEventId, string? OutlookWebLink, Guid TransactionId, string DesiredAction);
}
