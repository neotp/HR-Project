using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace HrProject.AttendanceWorker;

public sealed class WifiAttendanceImporter(
    IConfiguration configuration,
    IOptions<WifiAttendanceOptions> options,
    NpgsqlDataSource dataSource,
    ILogger<WifiAttendanceImporter> logger)
{
    private readonly WifiAttendanceOptions settings = options.Value;
    private readonly TimeZoneInfo attendanceZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");
    private const string SourceSystem = "WIFI";
    private bool warnedMissingConnection;

    public async Task<HashSet<AttendanceKey>> ImportAsync(CancellationToken token)
    {
        var affected = new HashSet<AttendanceKey>();
        if (!settings.Enabled) return affected;

        var connectionString = configuration.GetConnectionString("WifiAttendanceDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (!warnedMissingConnection)
            {
                logger.LogWarning("Wi-Fi attendance is enabled but ConnectionStrings:WifiAttendanceDatabase is not configured");
                warnedMissingConnection = true;
            }
            return affected;
        }
        warnedMissingConnection = false;

        try
        {
            var employeesByMac = await LoadEmployeesByMac(token);
            if (employeesByMac.Count == 0) return affected;

            await using var source = new NpgsqlConnection(connectionString);
            await source.OpenAsync(token);
            var table = await ResolveSourceTable(source, token);
            var lastWindowEnd = await LoadLastWindowEnd(token);
            var cursor = lastWindowEnd?.AddHours(-Math.Clamp(settings.OverlapHours, 1, 168))
                ?? DateTime.UtcNow.AddDays(-Math.Clamp(settings.InitialLookbackDays, 1, 3650));
            var windowEnd = DateTime.UtcNow;
            var batchSize = Math.Clamp(settings.BatchSize, 100, 20000);
            var offset = 0;
            var macs = employeesByMac.Keys.ToArray();

            while (true)
            {
                var connections = await ReadBatch(source, table, macs, cursor, windowEnd, batchSize, offset, token);
                if (connections.Count == 0) break;
                await SaveConnections(connections, employeesByMac, affected, token);
                if (connections.Count < batchSize) break;
                offset += connections.Count;
            }

            await SaveSyncSuccess(table, windowEnd, token);
            return affected;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Wi-Fi attendance import failed");
            await SaveSyncError(exception.Message, token);
            return affected;
        }
    }

    private async Task<Dictionary<string, string>> LoadEmployeesByMac(CancellationToken token)
    {
        const string sql = """
            SELECT regexp_replace(lower(company.mac_address), '[^0-9a-f]', '', 'g') AS mac,
                   min(employee.employee_code) AS employee_code,
                   count(DISTINCT employee.employee_code) AS employee_count
            FROM public.employees employee
            JOIN public.employee_company_info company ON company.employee_id = employee.id
            WHERE employee.is_active = TRUE
              AND COALESCE(company.exclude_attendance_calculation, FALSE) = FALSE
              AND NULLIF(btrim(company.mac_address), '') IS NOT NULL
            GROUP BY regexp_replace(lower(company.mac_address), '[^0-9a-f]', '', 'g')
            """;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var mac = reader.GetString(0);
            if (mac.Length != 12 || !mac.All(Uri.IsHexDigit)) continue;
            if (reader.GetInt64(2) != 1)
            {
                logger.LogWarning("MAC address {Mac} belongs to multiple active employees; it will be skipped", mac);
                continue;
            }
            result.Add(mac, reader.GetString(1));
        }
        return result;
    }

    private async Task<SourceTable> ResolveSourceTable(NpgsqlConnection source, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(settings.SourceTable))
            return new SourceTable(settings.SourceSchema ?? "public", settings.SourceTable);

        const string sql = """
            SELECT table_schema, table_name
            FROM information_schema.columns
            WHERE column_name IN ('calling_station_id', 'start_time', 'session_id')
              AND table_schema NOT IN ('pg_catalog', 'information_schema')
              AND (@schema IS NULL OR table_schema = @schema)
            GROUP BY table_schema, table_name
            HAVING count(DISTINCT column_name) = 3
            ORDER BY table_schema, table_name
            """;
        await using var command = new NpgsqlCommand(sql, source);
        command.Parameters.Add(new NpgsqlParameter<string?>("schema", settings.SourceSchema));
        var tables = new List<SourceTable>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) tables.Add(new SourceTable(reader.GetString(0), reader.GetString(1)));
        if (tables.Count != 1)
            throw new InvalidOperationException($"Found {tables.Count} Wi-Fi source tables with session_id, calling_station_id and start_time; configure WifiAttendance:SourceSchema and SourceTable explicitly.");
        return tables[0];
    }

    private static async Task<List<WifiConnection>> ReadBatch(
        NpgsqlConnection source, SourceTable table, string[] macs,
        DateTime cursor, DateTime windowEnd, int batchSize, int offset, CancellationToken token)
    {
        var sql = $"""
            SELECT session_id::text, calling_station_id::text, start_time
            FROM {Quote(table.Schema)}.{Quote(table.Table)}
            WHERE start_time >= @cursor AND start_time < @window_end
              AND regexp_replace(lower(calling_station_id), '[^0-9a-f]', '', 'g') = ANY(@macs)
            ORDER BY start_time, calling_station_id, session_id
            LIMIT @limit OFFSET @offset
            """;
        await using var command = new NpgsqlCommand(sql, source) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("cursor", cursor);
        command.Parameters.AddWithValue("window_end", windowEnd);
        command.Parameters.AddWithValue("macs", macs);
        command.Parameters.AddWithValue("limit", batchSize);
        command.Parameters.AddWithValue("offset", offset);
        var result = new List<WifiConnection>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(2)) continue;
            var mac = NormalizeMac(reader.GetString(1));
            if (mac.Length != 12) continue;
            result.Add(new WifiConnection(reader.IsDBNull(0) ? null : reader.GetString(0), mac,
                DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)));
        }
        return result;
    }

    private async Task SaveConnections(
        IReadOnlyList<WifiConnection> connections, IReadOnlyDictionary<string, string> employeesByMac,
        HashSet<AttendanceKey> affected, CancellationToken token)
    {
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var stage = new NpgsqlCommand("""
            CREATE TEMP TABLE attendance_wifi_stage
            (employee_id varchar(50), mac_address varchar(12), source_session_id text, start_at timestamptz)
            ON COMMIT DROP
            """, connection, transaction))
            await stage.ExecuteNonQueryAsync(token);

        await using (var importer = await connection.BeginBinaryImportAsync("""
            COPY attendance_wifi_stage(employee_id, mac_address, source_session_id, start_at)
            FROM STDIN (FORMAT BINARY)
            """, token))
        {
            foreach (var item in connections)
            {
                if (!employeesByMac.TryGetValue(item.Mac, out var employeeId)) continue;
                await importer.StartRowAsync(token);
                await importer.WriteAsync(employeeId, NpgsqlDbType.Varchar, token);
                await importer.WriteAsync(item.Mac, NpgsqlDbType.Varchar, token);
                if (item.SessionId is null) await importer.WriteNullAsync(token);
                else await importer.WriteAsync(item.SessionId, NpgsqlDbType.Text, token);
                await importer.WriteAsync(item.StartAt, NpgsqlDbType.TimestampTz, token);
                var local = TimeZoneInfo.ConvertTimeFromUtc(item.StartAt, attendanceZone);
                affected.Add(new AttendanceKey(employeeId, DateOnly.FromDateTime(local)));
            }
            await importer.CompleteAsync(token);
        }

        await using (var merge = new NpgsqlCommand("""
            INSERT INTO public.attendance_wifi_connections(employee_id, mac_address, source_session_id, start_at)
            SELECT employee_id, mac_address, source_session_id, start_at
            FROM attendance_wifi_stage
            ON CONFLICT DO NOTHING
            """, connection, transaction))
            await merge.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    private async Task<DateTime?> LoadLastWindowEnd(CancellationToken token)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT last_window_end FROM public.attendance_wifi_sync_state WHERE source_system = 'WIFI'");
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc);
    }

    private async Task SaveSyncSuccess(SourceTable table, DateTime windowEnd, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO public.attendance_wifi_sync_state
                (source_system, source_schema, source_table, last_window_end, last_success_at, last_error)
            VALUES ('WIFI', @schema, @table, @window_end, CURRENT_TIMESTAMP, NULL)
            ON CONFLICT (source_system) DO UPDATE SET
                source_schema = EXCLUDED.source_schema, source_table = EXCLUDED.source_table,
                last_window_end = EXCLUDED.last_window_end, last_success_at = CURRENT_TIMESTAMP,
                last_error = NULL, updated_at = CURRENT_TIMESTAMP
            """);
        command.Parameters.AddWithValue("schema", table.Schema);
        command.Parameters.AddWithValue("table", table.Table);
        command.Parameters.AddWithValue("window_end", windowEnd);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task SaveSyncError(string error, CancellationToken token)
    {
        try
        {
            await using var command = dataSource.CreateCommand("""
                INSERT INTO public.attendance_wifi_sync_state(source_system, last_error)
                VALUES ('WIFI', @error)
                ON CONFLICT (source_system) DO UPDATE SET
                    last_error = EXCLUDED.last_error, updated_at = CURRENT_TIMESTAMP
                """);
            command.Parameters.AddWithValue("error", error.Length > 2000 ? error[..2000] : error);
            await command.ExecuteNonQueryAsync(token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not save Wi-Fi attendance sync error");
        }
    }

    private static string NormalizeMac(string value) =>
        new(value.Where(Uri.IsHexDigit).Select(char.ToLowerInvariant).ToArray());

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private sealed record SourceTable(string Schema, string Table);
    private sealed record WifiConnection(string? SessionId, string Mac, DateTime StartAt);
}
