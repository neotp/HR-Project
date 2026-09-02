using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace HrProject.AttendanceWorker;

public sealed class HikvisionAttendanceWorker(
    IConfiguration configuration,
    IOptions<AttendanceWorkerOptions> options,
    NpgsqlDataSource dataSource,
    AttendanceProcessor processor,
    ILogger<HikvisionAttendanceWorker> logger) : BackgroundService
{
    private const string SourceSystem = "HIKVISION";
    private readonly AttendanceWorkerOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled) return;
        var interval = TimeSpan.FromSeconds(Math.Clamp(settings.PollIntervalSeconds, 15, 3600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var affected = await ImportAsync(stoppingToken);
                await processor.RecalculateAsync(affected, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Attendance import cycle failed");
                await SaveSyncError(exception.Message, stoppingToken);
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task<HashSet<AttendanceKey>> ImportAsync(CancellationToken token)
    {
        var sourceConnectionString = configuration.GetConnectionString("HikvisionDatabase")
            ?? throw new InvalidOperationException("Connection string 'HikvisionDatabase' is not configured.");
        await using var source = new SqlConnection(sourceConnectionString);
        await source.OpenAsync(token);

        var sourceTable = await ResolveSourceTable(source, token);
        var lastCapturedAt = await LoadLastCapturedAt(token);
        var cursor = lastCapturedAt?.AddMinutes(-5)
            ?? LocalNow().Date.AddDays(-Math.Clamp(settings.InitialLookbackDays, 1, 3650));
        var latest = lastCapturedAt;
        var affected = new HashSet<AttendanceKey>();
        var batchSize = Math.Clamp(settings.BatchSize, 100, 20000);

        while (true)
        {
            var scans = await ReadSourceBatch(source, sourceTable, cursor, batchSize, token);
            if (scans.Count == 0) break;
            await SaveRawScans(scans, affected, token);
            var batchLatest = scans.Max(x => x.CapturedAt);
            latest = !latest.HasValue || batchLatest > latest ? batchLatest : latest;
            if (scans.Count < batchSize) break;
            cursor = batchLatest;
        }

        await SaveSyncSuccess(sourceTable, latest, token);
        return affected;
    }

    private async Task<SourceTable> ResolveSourceTable(SqlConnection source, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(settings.SourceTable))
            return new SourceTable(settings.SourceSchema ?? "dbo", settings.SourceTable);

        const string sql = """
            SELECT TOP (1) s.name, t.name
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id
            GROUP BY s.name, t.name
            HAVING COUNT(DISTINCT CASE WHEN LOWER(c.name) IN
                ('id','datetime','status','device','deviceno') THEN LOWER(c.name) END) = 5
            ORDER BY s.name, t.name
            """;
        await using var command = new SqlCommand(sql, source);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new InvalidOperationException(
                "ไม่พบตาราง Hikvision ที่มีคอลัมน์ ID, datetime, Status, Device และ DeviceNo");
        return new SourceTable(reader.GetString(0), reader.GetString(1));
    }

    private static async Task<List<SourceScan>> ReadSourceBatch(
        SqlConnection source, SourceTable table, DateTime cursor, int batchSize, CancellationToken token)
    {
        var qualifiedTable = $"{Quote(table.Schema)}.{Quote(table.Table)}";
        var sql = $"""
            SELECT TOP (@limit)
                   CONVERT(nvarchar(50), [ID]), CONVERT(datetime2, [datetime]),
                   CONVERT(nvarchar(50), [Status]), CONVERT(nvarchar(200), [Device]),
                   CONVERT(nvarchar(300), [DeviceNo])
            FROM {qualifiedTable}
            WHERE [datetime] > @cursor
            ORDER BY [datetime], [ID]
            """;
        await using var command = new SqlCommand(sql, source) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("limit", batchSize);
        command.Parameters.AddWithValue("cursor", cursor);
        var result = new List<SourceScan>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            result.Add(new SourceScan(
                reader.GetString(0).Trim(), reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return result;
    }

    private async Task SaveRawScans(
        IReadOnlyCollection<SourceScan> scans, HashSet<AttendanceKey> affected, CancellationToken token)
    {
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var createStage = new NpgsqlCommand("""
            CREATE TEMP TABLE attendance_import_stage
            (
                source_system varchar(50), source_employee_id varchar(50),
                captured_at timestamp, source_status varchar(50),
                device_name varchar(200), device_no varchar(300), source_payload jsonb
            ) ON COMMIT DROP
            """, connection, transaction))
        {
            await createStage.ExecuteNonQueryAsync(token);
        }

        await using (var importer = await connection.BeginBinaryImportAsync("""
            COPY attendance_import_stage
                (source_system, source_employee_id, captured_at, source_status,
                 device_name, device_no, source_payload)
            FROM STDIN (FORMAT BINARY)
            """, token))
        {
            foreach (var scan in scans)
            {
                await importer.StartRowAsync(token);
                await importer.WriteAsync(SourceSystem, NpgsqlDbType.Varchar, token);
                await importer.WriteAsync(scan.EmployeeId, NpgsqlDbType.Varchar, token);
                await importer.WriteAsync(scan.CapturedAt, NpgsqlDbType.Timestamp, token);
                if (scan.Status is null) await importer.WriteNullAsync(token);
                else await importer.WriteAsync(scan.Status, NpgsqlDbType.Varchar, token);
                if (scan.Device is null) await importer.WriteNullAsync(token);
                else await importer.WriteAsync(scan.Device, NpgsqlDbType.Varchar, token);
                if (scan.DeviceNo is null) await importer.WriteNullAsync(token);
                else await importer.WriteAsync(scan.DeviceNo, NpgsqlDbType.Varchar, token);
                await importer.WriteAsync(JsonSerializer.Serialize(scan), NpgsqlDbType.Jsonb, token);
                affected.Add(new AttendanceKey(scan.EmployeeId, DateOnly.FromDateTime(scan.CapturedAt)));
            }
            await importer.CompleteAsync(token);
        }

        const string mergeSql = """
            INSERT INTO public.attendance_raw_scans
                (source_system, source_employee_id, captured_at, source_status,
                 device_name, device_no, source_payload)
            SELECT source_system, source_employee_id, captured_at, source_status,
                   device_name, device_no, source_payload
            FROM attendance_import_stage
            ON CONFLICT DO NOTHING
            """;
        await using var merge = new NpgsqlCommand(mergeSql, connection, transaction);
        await merge.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    private async Task<DateTime?> LoadLastCapturedAt(CancellationToken token)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT last_captured_at FROM public.attendance_sync_states WHERE source_system = @source");
        command.Parameters.AddWithValue("source", SourceSystem);
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : Convert.ToDateTime(value);
    }

    private async Task SaveSyncSuccess(SourceTable table, DateTime? latest, CancellationToken token)
    {
        const string sql = """
            INSERT INTO public.attendance_sync_states
                (source_system, source_schema, source_table, last_captured_at,
                 last_success_at, last_error, updated_at)
            VALUES (@source, @schema, @table, @latest, CURRENT_TIMESTAMP, NULL, CURRENT_TIMESTAMP)
            ON CONFLICT (source_system) DO UPDATE SET
                source_schema = EXCLUDED.source_schema, source_table = EXCLUDED.source_table,
                last_captured_at = GREATEST(attendance_sync_states.last_captured_at, EXCLUDED.last_captured_at),
                last_success_at = CURRENT_TIMESTAMP, last_error = NULL, updated_at = CURRENT_TIMESTAMP
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("source", SourceSystem);
        command.Parameters.AddWithValue("schema", table.Schema);
        command.Parameters.AddWithValue("table", table.Table);
        command.Parameters.Add(new NpgsqlParameter<DateTime?>("latest", latest));
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task SaveSyncError(string error, CancellationToken token)
    {
        try
        {
            await using var command = dataSource.CreateCommand("""
                INSERT INTO public.attendance_sync_states(source_system, last_error)
                VALUES (@source, @error)
                ON CONFLICT (source_system) DO UPDATE SET
                    last_error = EXCLUDED.last_error, updated_at = CURRENT_TIMESTAMP
                """);
            command.Parameters.AddWithValue("source", SourceSystem);
            command.Parameters.AddWithValue("error", error.Length > 2000 ? error[..2000] : error);
            await command.ExecuteNonQueryAsync(token);
        }
        catch { }
    }

    private DateTime LocalNow()
    {
        try { return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, settings.TimeZone).DateTime; }
        catch { return DateTime.UtcNow.AddHours(7); }
    }

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    private sealed record SourceTable(string Schema, string Table);
    private sealed record SourceScan(string EmployeeId, DateTime CapturedAt, string? Status, string? Device, string? DeviceNo);
}

public readonly record struct AttendanceKey(string EmployeeId, DateOnly WorkDate);
