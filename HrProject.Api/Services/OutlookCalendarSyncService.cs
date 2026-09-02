using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace HrProject.Api.Services;

public sealed class OutlookCalendarSyncService(
    NpgsqlDataSource dataSource,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration)
{
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? cachedAccessToken;
    private DateTimeOffset accessTokenExpiresAt;

    public async Task SyncAsync(long documentId, CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("OutlookCalendar:Enabled", true))
            return;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await AcquireLock(connection, documentId, cancellationToken);
        try
        {
            var document = await LoadDocument(connection, documentId, cancellationToken)
                ?? throw new InvalidOperationException($"ไม่พบเอกสารการลา ID {documentId}");
            var link = await EnsureLink(connection, documentId, cancellationToken);

            if (document.Status is "REJECTED" or "CANCELLED")
            {
                await DeleteEvent(connection, document, link, cancellationToken);
                return;
            }

            if (string.IsNullOrWhiteSpace(document.EmployeeEmail) ||
                !document.EmployeeEmail.Contains('@', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"พนักงาน {document.EmployeeCode} ยังไม่มี Email สำหรับ Outlook");
            }

            if (link.OutlookEventId is null)
                await CreateEvent(connection, document, link, cancellationToken);
            else
                await UpdateEvent(connection, document, link, cancellationToken);
        }
        catch (Exception exception)
        {
            // Graph failures are persisted for the background retry worker. Database
            // migration/configuration failures cannot be persisted and bubble to the caller.
            if (await LinkTableExists(connection, cancellationToken))
            {
                await MarkFailed(connection, documentId, null, null, SafeError(exception), cancellationToken);
            }
            throw;
        }
        finally
        {
            await ReleaseLock(connection, documentId);
        }
    }

    public async Task<IReadOnlyList<long>> LoadRetryDocumentIds(
        int limit,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await LoadRetryDocumentIdsCore(limit, cancellationToken);
            }
            catch (NpgsqlException exception) when (
                exception.IsTransient && attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                // A pooled connector may have been closed by the database server
                // or an intermediate firewall. Npgsql removes the broken connector;
                // the next attempt obtains a fresh physical connection.
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyList<long>> LoadRetryDocumentIdsCore(
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT leave_document_id
            FROM public.leave_calendar_events
            WHERE sync_status IN ('PENDING', 'FAILED')
              AND (last_attempted_at IS NULL OR last_attempted_at <= CURRENT_TIMESTAMP - INTERVAL '1 minute')
            ORDER BY COALESCE(last_attempted_at, created_at), id
            LIMIT @limit
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        var result = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetInt64(0));
        return result;
    }

    private async Task CreateEvent(
        NpgsqlConnection connection,
        CalendarDocument document,
        CalendarLink link,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessToken(cancellationToken);
        using var request = CreateGraphRequest(
            HttpMethod.Post,
            $"users/{Uri.EscapeDataString(document.EmployeeEmail!)}/calendar/events",
            token);
        request.Content = JsonContent.Create(BuildEvent(document, link.TransactionId.ToString()));
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw GraphException("สร้าง Outlook Calendar", response.StatusCode, responseText);

        using var json = JsonDocument.Parse(responseText);
        var root = json.RootElement;
        var eventId = root.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Microsoft Graph ไม่ได้ส่ง Event ID กลับมา");
        var webLink = root.TryGetProperty("webLink", out var webLinkProperty)
            ? webLinkProperty.GetString()
            : null;
        await MarkSucceeded(connection, document.Id, "CREATE", document.EmployeeEmail,
            eventId, webLink, "SYNCED", cancellationToken);
    }

    private async Task UpdateEvent(
        NpgsqlConnection connection,
        CalendarDocument document,
        CalendarLink link,
        CancellationToken cancellationToken)
    {
        var mailbox = string.IsNullOrWhiteSpace(link.EmployeeEmail)
            ? document.EmployeeEmail!
            : link.EmployeeEmail;
        var token = await GetAccessToken(cancellationToken);
        using var request = CreateGraphRequest(
            HttpMethod.Patch,
            $"users/{Uri.EscapeDataString(mailbox)}/events/{Uri.EscapeDataString(link.OutlookEventId!)}",
            token);
        request.Content = JsonContent.Create(BuildEvent(document, null));
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw GraphException("อัปเดต Outlook Calendar", response.StatusCode, responseText);

        string? webLink = link.OutlookWebLink;
        if (!string.IsNullOrWhiteSpace(responseText))
        {
            using var json = JsonDocument.Parse(responseText);
            if (json.RootElement.TryGetProperty("webLink", out var webLinkProperty))
                webLink = webLinkProperty.GetString();
        }
        await MarkSucceeded(connection, document.Id, "UPDATE", mailbox,
            link.OutlookEventId, webLink, "SYNCED", cancellationToken);
    }

    private async Task DeleteEvent(
        NpgsqlConnection connection,
        CalendarDocument document,
        CalendarLink link,
        CancellationToken cancellationToken)
    {
        if (link.OutlookEventId is null || string.IsNullOrWhiteSpace(link.EmployeeEmail))
        {
            await MarkSucceeded(connection, document.Id, "DELETE", link.EmployeeEmail,
                null, null, "DELETED", cancellationToken);
            return;
        }

        var token = await GetAccessToken(cancellationToken);
        using var request = CreateGraphRequest(
            HttpMethod.Delete,
            $"users/{Uri.EscapeDataString(link.EmployeeEmail)}/events/{Uri.EscapeDataString(link.OutlookEventId)}",
            token);
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            throw GraphException("ลบ Outlook Calendar", response.StatusCode, responseText);
        }
        await MarkSucceeded(connection, document.Id, "DELETE", link.EmployeeEmail,
            null, null, "DELETED", cancellationToken);
    }

    private Dictionary<string, object?> BuildEvent(CalendarDocument document, string? transactionId)
    {
        var isPending = document.Status == "PENDING_APPROVAL";
        var endTime = AddWorkingHours(document.StartTime, document.LeaveHours);
        // Microsoft Graph expects a Gregorian calendar date. Formatting with the
        // active Thai culture converts 2026 to the Buddhist year 2569.
        var graphDate = document.LeaveDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var clientBaseUrl = (configuration["Application:ClientBaseUrl"] ?? "http://localhost:5043").TrimEnd('/');
        var documentUrl = $"{clientBaseUrl}/leave/documents?documentId={document.Id}";
        var body = $"""
            <p><strong>เลขที่เอกสาร:</strong> {WebUtility.HtmlEncode(document.DocumentNo)}</p>
            <p><strong>ประเภทการลา:</strong> {WebUtility.HtmlEncode(document.LeaveTypeName)}</p>
            <p><a href="{WebUtility.HtmlEncode(documentUrl)}">เปิดเอกสารในระบบ HR</a></p>
            """;

        var calendarEvent = new Dictionary<string, object?>
        {
            ["subject"] = $"Test [{(isPending ? "รออนุมัติ" : "อนุมัติแล้ว")}] {document.LeaveTypeName} - {document.DocumentNo}",
            ["body"] = new { contentType = "HTML", content = body },
            ["start"] = new { dateTime = $"{graphDate}T{document.StartTime:HH:mm:ss}", timeZone = TimeZone },
            ["end"] = new { dateTime = $"{graphDate}T{endTime:HH:mm:ss}", timeZone = TimeZone },
            ["showAs"] = isPending ? "tentative" : "oof",
            ["sensitivity"] = "private",
            ["isReminderOn"] = false
        };
        if (!string.IsNullOrWhiteSpace(transactionId))
            calendarEvent["transactionId"] = transactionId;
        return calendarEvent;
    }

    private string TimeZone => configuration["OutlookCalendar:TimeZone"] ?? "SE Asia Standard Time";

    private static TimeOnly AddWorkingHours(TimeOnly start, decimal hours)
    {
        var remainingMinutes = (int)Math.Round(hours * 60m, MidpointRounding.AwayFromZero);
        var current = start;
        var noon = new TimeOnly(12, 0);
        var afternoon = new TimeOnly(13, 0);
        if (current >= noon && current < afternoon) current = afternoon;

        if (current < noon)
        {
            var untilNoon = (int)(noon.ToTimeSpan() - current.ToTimeSpan()).TotalMinutes;
            if (remainingMinutes <= untilNoon) return current.AddMinutes(remainingMinutes);
            remainingMinutes -= untilNoon;
            current = afternoon;
        }
        return current.AddMinutes(remainingMinutes);
    }

    private async Task<string> GetAccessToken(CancellationToken cancellationToken)
    {
        if (cachedAccessToken is not null && accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return cachedAccessToken;

        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedAccessToken is not null && accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return cachedAccessToken;
            var tenantId = Required("AzureAd:TenantId");
            var clientId = Required("AzureAd:ClientId");
            var clientSecret = Required("AzureAd:ClientSecret");
            using var response = await httpClientFactory.CreateClient().PostAsync(
                $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["scope"] = "https://graph.microsoft.com/.default",
                    ["grant_type"] = "client_credentials"
                }), cancellationToken);
            var jsonText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw GraphException("ขอ Access Token", response.StatusCode, jsonText);
            using var json = JsonDocument.Parse(jsonText);
            cachedAccessToken = json.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Microsoft Graph ไม่ได้ส่ง Access Token กลับมา");
            var expiresIn = json.RootElement.TryGetProperty("expires_in", out var expiresProperty)
                ? expiresProperty.GetInt32()
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
        request.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\", outlook.timezone=\"SE Asia Standard Time\"");
        return request;
    }

    private static async Task<CalendarDocument?> LoadDocument(
        NpgsqlConnection connection,
        long documentId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.id, d.document_no, d.creator_employee_id, d.creator_name,
                   NULLIF(BTRIM(b.email_address), ''), t.name_th, d.leave_date,
                   d.start_time, d.leave_hours, d.status
            FROM public.leave_documents d
            JOIN public.leave_types t ON t.id = d.leave_type_id
            LEFT JOIN public.employees e ON e.employee_code = d.creator_employee_id
            LEFT JOIN public.employee_basic_info b ON b.employee_id = e.id
            WHERE d.id = @id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CalendarDocument(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
                reader.GetFieldValue<DateOnly>(6), reader.GetFieldValue<TimeOnly>(7),
                reader.GetDecimal(8), reader.GetString(9))
            : null;
    }

    private static async Task<CalendarLink> EnsureLink(
        NpgsqlConnection connection,
        long documentId,
        CancellationToken cancellationToken)
    {
        const string insertSql = """
            INSERT INTO public.leave_calendar_events
                (leave_document_id, transaction_id, sync_status)
            VALUES (@document_id, @transaction_id, 'PENDING')
            ON CONFLICT (leave_document_id) DO NOTHING
            """;
        await using (var insert = new NpgsqlCommand(insertSql, connection))
        {
            insert.Parameters.AddWithValue("document_id", documentId);
            insert.Parameters.AddWithValue("transaction_id", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        const string selectSql = """
            SELECT transaction_id, employee_email, outlook_event_id, outlook_web_link
            FROM public.leave_calendar_events WHERE leave_document_id = @document_id
            """;
        await using var command = new NpgsqlCommand(selectSql, connection);
        command.Parameters.AddWithValue("document_id", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("ไม่สามารถสร้างรายการเชื่อม Outlook Calendar ได้");
        return new CalendarLink(
            reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task MarkSucceeded(
        NpgsqlConnection connection, long documentId, string action, string? email,
        string? eventId, string? webLink, string status, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE public.leave_calendar_events
            SET employee_email = COALESCE(@email, employee_email),
                outlook_event_id = @event_id, outlook_web_link = @web_link,
                sync_status = @status, last_action = @action,
                last_sync_error = NULL, last_attempted_at = CURRENT_TIMESTAMP,
                synced_at = CURRENT_TIMESTAMP, updated_at = CURRENT_TIMESTAMP
            WHERE leave_document_id = @document_id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<string?>("email", email));
        command.Parameters.Add(new NpgsqlParameter<string?>("event_id", eventId));
        command.Parameters.Add(new NpgsqlParameter<string?>("web_link", webLink));
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("document_id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkFailed(
        NpgsqlConnection connection, long documentId, string? action, string? email,
        string error, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE public.leave_calendar_events
            SET employee_email = COALESCE(@email, employee_email),
                sync_status = 'FAILED', last_action = COALESCE(@action, last_action),
                retry_count = retry_count + 1, last_sync_error = @error,
                last_attempted_at = CURRENT_TIMESTAMP, updated_at = CURRENT_TIMESTAMP
            WHERE leave_document_id = @document_id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<string?>("email", email));
        command.Parameters.Add(new NpgsqlParameter<string?>("action", action));
        command.Parameters.AddWithValue("error", error);
        command.Parameters.AddWithValue("document_id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> LinkTableExists(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('public.leave_calendar_events') IS NOT NULL", connection);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task AcquireLock(NpgsqlConnection connection, long documentId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtextextended('leave-calendar:' || CAST(@id AS text), 0))", connection);
        command.Parameters.AddWithValue("id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReleaseLock(NpgsqlConnection connection, long documentId)
    {
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtextextended('leave-calendar:' || CAST(@id AS text), 0))", connection);
            command.Parameters.AddWithValue("id", documentId);
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // Closing the PostgreSQL session also releases the session advisory lock.
        }
    }

    private string Required(string key) => configuration[key]
        ?? throw new InvalidOperationException($"ยังไม่ได้ตั้งค่า {key}");

    private static InvalidOperationException GraphException(string action, HttpStatusCode status, string detail) =>
        new($"Microsoft Graph {action}ไม่สำเร็จ ({(int)status}): {SafeError(detail)}");

    private static string SafeError(Exception exception) => SafeError(exception.Message);
    private static string SafeError(string text) => text.Length > 1000 ? text[..1000] : text;

    private sealed record CalendarDocument(
        long Id, string DocumentNo, string EmployeeCode, string EmployeeName,
        string? EmployeeEmail, string LeaveTypeName, DateOnly LeaveDate,
        TimeOnly StartTime, decimal LeaveHours, string Status);

    private sealed record CalendarLink(
        Guid TransactionId, string? EmployeeEmail, string? OutlookEventId, string? OutlookWebLink);
}
