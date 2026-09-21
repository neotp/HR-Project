using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Npgsql;
using NpgsqlTypes;

namespace HrProject.Api.Services;

public sealed class LotusNotesEmployeeOutboxWorker(
    NpgsqlDataSource dataSource,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    LotusNotesOutboxSignal signal,
    WorkflowEmailNotificationService workflowNotificationService,
    ILogger<LotusNotesEmployeeOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = ReadSettings();
                if (settings is null)
                {
                    logger.LogWarning("Lotus Notes employee sync is waiting for LOTUS_NOTES_ENDPOINT, LOTUS_NOTES_USERNAME, LOTUS_NOTES_PASSWORD and LOTUS_NOTES_DATABASE configuration");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                // Drain anything left from a previous shutdown on startup, then sleep
                // until conversion or an administrator's Retry explicitly wakes us.
                while (!stoppingToken.IsCancellationRequested)
                {
                    var item = await ClaimNext(stoppingToken);
                    if (item is null) break;
                    await Send(item, settings, stoppingToken);
                }

                await signal.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                logger.LogWarning("Lotus Notes employee outbox table does not exist; run migration 066");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Lotus Notes employee sync cycle failed");
                // A slow fallback is retained only for infrastructure recovery. Normal
                // sends are event-driven and do not poll the table on an interval.
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private async Task<OutboxItem?> ClaimNext(CancellationToken token)
    {
        const string sql = """
            WITH candidate AS
            (
                SELECT id
                FROM public.lotus_notes_employee_outbox
                WHERE status = 'PENDING'
                   OR (status = 'PROCESSING' AND last_attempt_at < CURRENT_TIMESTAMP - INTERVAL '10 minutes')
                ORDER BY created_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE public.lotus_notes_employee_outbox target
            SET status='PROCESSING', attempt_count=attempt_count+1,
                last_attempt_at=CURRENT_TIMESTAMP, updated_at=CURRENT_TIMESTAMP,
                last_error=NULL, response_summary=NULL
            FROM candidate
            WHERE target.id=candidate.id
            RETURNING target.id, target.employee_code, target.database_name,
                      target.external_key, target.payload::text
            """;
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(token);
        OutboxItem? result = null;
        if (await reader.ReadAsync(token))
            result = new OutboxItem(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4));
        await reader.DisposeAsync();
        await transaction.CommitAsync(token);
        return result;
    }

    private async Task Send(OutboxItem item, Settings settings, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}")));
            request.Headers.TryAddWithoutValidation("SOAPAction", "\"FNPUSH\"");
            var database = string.IsNullOrWhiteSpace(item.DatabaseName) ? settings.Database : item.DatabaseName;
            request.Content = new StringContent(BuildSoapEnvelope(database, item.ExternalKey, item.Payload),
                Encoding.UTF8, "text/xml");

            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            using var response = await client.SendAsync(request, token);
            var responseText = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Lotus Notes Gateway returned HTTP {(int)response.StatusCode}: {Limit(responseText, 1500)}");

            var gatewayResult = ReadGatewayResult(responseText);

            await Complete(item.Id, "SUCCESS", null, Limit(gatewayResult, 4000), token);
            logger.LogInformation("Lotus Notes Gateway accepted employee outbox {OutboxId}", item.Id);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await Complete(item.Id, "FAILED", Limit(exception.Message, 4000), null, token);
            logger.LogWarning(exception, "Lotus Notes employee outbox {OutboxId} failed; manual retry is required", item.Id);
            try
            {
                await workflowNotificationService.SendAsync(
                    "LOTUS_NOTES_EMPLOYEE_SYNC", item.EmployeeId,
                    "ส่งข้อมูลพนักงานไป Lotus Notes ไม่สำเร็จ",
                    $"Outbox ID: {item.Id}\nรหัสพนักงาน: {item.EmployeeId}\nข้อผิดพลาด: {Limit(exception.Message, 1000)}",
                    "/system/lotus-notes-sync", CancellationToken.None);
            }
            catch (Exception emailException)
            {
                logger.LogError(emailException,
                    "Lotus Notes outbox {OutboxId} failed and its email notification also failed", item.Id);
            }
        }
    }

    private async Task Complete(long id, string status, string? error, string? response, CancellationToken token)
    {
        const string sql = """
            UPDATE public.lotus_notes_employee_outbox
            SET status=@status, last_error=@error, response_summary=@response,
                succeeded_at=CASE WHEN @status='SUCCESS' THEN CURRENT_TIMESTAMP ELSE NULL END,
                updated_at=CURRENT_TIMESTAMP
            WHERE id=@id AND status='PROCESSING'
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.Add("error", NpgsqlDbType.Text).Value = (object?)error ?? DBNull.Value;
        command.Parameters.Add("response", NpgsqlDbType.Text).Value = (object?)response ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(token);
    }

    private Settings? ReadSettings()
    {
        var endpoint = Environment.GetEnvironmentVariable("LOTUS_NOTES_ENDPOINT") ?? configuration["LotusNotes:Endpoint"];
        var username = Environment.GetEnvironmentVariable("LOTUS_NOTES_USERNAME") ?? configuration["LotusNotes:Username"];
        var password = Environment.GetEnvironmentVariable("LOTUS_NOTES_PASSWORD") ?? configuration["LotusNotes:Password"];
        var database = Environment.GetEnvironmentVariable("LOTUS_NOTES_DATABASE") ?? configuration["LotusNotes:Database"];
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
               !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password) &&
               !string.IsNullOrWhiteSpace(database)
            ? new Settings(uri, username, password, database)
            : null;
    }

    private static string Limit(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private static string BuildSoapEnvelope(string database, string key, string data) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"
                          xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                          xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <soapenv:Body>
            <ns1:FNPUSH xmlns:ns1="urn:DefaultNamespace"
                        soapenv:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <STRDATABASE xsi:type="xsd:string">{SecurityElement.Escape(database)}</STRDATABASE>
              <STRKEY xsi:type="xsd:string">{SecurityElement.Escape(key)}</STRKEY>
              <STRDATA xsi:type="xsd:string">{SecurityElement.Escape(data)}</STRDATA>
            </ns1:FNPUSH>
          </soapenv:Body>
        </soapenv:Envelope>
        """;

    private static string ReadGatewayResult(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText)) return "Gateway accepted the request (empty response).";
        var document = XDocument.Parse(responseText);
        var fault = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "Fault");
        if (fault is not null)
            throw new HttpRequestException($"Lotus Notes SOAP Fault: {Limit(fault.Value.Trim(), 1500)}");
        var result = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "FNPUSHReturn")?.Value;
        return string.IsNullOrWhiteSpace(result) ? "Gateway accepted the request." : result.Trim();
    }

    private sealed record Settings(Uri Endpoint, string Username, string Password, string Database);
    private sealed record OutboxItem(
        long Id, string EmployeeId, string DatabaseName, string ExternalKey, string Payload);
}
