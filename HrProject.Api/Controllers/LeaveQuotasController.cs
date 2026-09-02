using System.Text.Json;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/leave-quotas")]
public sealed class LeaveQuotasController(NpgsqlDataSource dataSource) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LeaveQuotaDto>>> GetAll(
        [FromQuery] int? year,
        [FromQuery] string? employeeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT q.id, q.employee_id, q.leave_type_id, t.name_th, q.quota_year,
                   q.quota_hours, usage.used_hours,
                   q.quota_hours - usage.used_hours,
                   q.notes, q.updated_at
            FROM public.leave_quotas q
            JOIN public.leave_types t ON t.id = q.leave_type_id
            LEFT JOIN LATERAL
            (
                SELECT COALESCE(SUM(d.leave_hours), 0) AS used_hours
                FROM public.leave_documents d
                WHERE d.creator_employee_id = q.employee_id
                  AND d.leave_type_id = q.leave_type_id
                  AND EXTRACT(YEAR FROM d.leave_date)::INT = q.quota_year
                  AND d.status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED')
            ) usage ON TRUE
            WHERE (@year IS NULL OR q.quota_year = @year)
              AND (@employee_id IS NULL OR q.employee_id = @employee_id)
            ORDER BY q.quota_year DESC, q.employee_id, t.name_th
            """;

        var result = new List<LeaveQuotaDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<int?>("year", year));
        command.Parameters.Add(new NpgsqlParameter<string?>("employee_id", employeeId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LeaveQuotaDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetInt16(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetFieldValue<DateTimeOffset>(9)));
        }

        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<LeaveQuotaDto>> Save(
        SaveLeaveQuotaRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.EmployeeId) ||
            request.LeaveTypeId <= 0 ||
            request.QuotaYear is < 2000 or > 2200 ||
            request.QuotaHours < 0 ||
            string.IsNullOrWhiteSpace(request.ActionBy) ||
            string.IsNullOrWhiteSpace(request.ActionByName))
        {
            return BadRequest("ข้อมูลโควต้าวันลาไม่ถูกต้อง");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string currentSql = """
            SELECT q.id, q.quota_hours, usage.used_hours, q.notes
            FROM public.leave_quotas q
            LEFT JOIN LATERAL
            (
                SELECT COALESCE(SUM(d.leave_hours), 0) AS used_hours
                FROM public.leave_documents d
                WHERE d.creator_employee_id = q.employee_id
                  AND d.leave_type_id = q.leave_type_id
                  AND EXTRACT(YEAR FROM d.leave_date)::INT = q.quota_year
                  AND d.status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED')
            ) usage ON TRUE
            WHERE q.employee_id = @employee_id
              AND q.leave_type_id = @leave_type_id
              AND q.quota_year = @quota_year
            FOR UPDATE OF q
            """;

        long? existingId = null;
        decimal oldQuotaHours = 0;
        decimal usedHours = 0;
        string? oldNotes = null;
        await using (var command = new NpgsqlCommand(currentSql, connection, transaction))
        {
            AddQuotaKeyParameters(command, request);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existingId = reader.GetInt64(0);
                oldQuotaHours = reader.GetDecimal(1);
                usedHours = reader.GetDecimal(2);
                oldNotes = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
        }

        if (usedHours > request.QuotaHours)
            return Conflict($"กำหนดโควต้าต่ำกว่าจำนวนที่ใช้ไปแล้ว ({usedHours:0.##} ชั่วโมง) ไม่ได้");

        const string upsertSql = """
            INSERT INTO public.leave_quotas
                (employee_id, leave_type_id, quota_year, quota_hours,
                 notes, created_by, created_by_name,
                 updated_by, updated_by_name)
            VALUES
                (@employee_id, @leave_type_id, @quota_year, @quota_hours,
                 @notes, @action_by, @action_by_name,
                 @action_by, @action_by_name)
            ON CONFLICT (employee_id, leave_type_id, quota_year)
            DO UPDATE SET
                quota_hours = EXCLUDED.quota_hours,
                notes = EXCLUDED.notes,
                updated_by = EXCLUDED.updated_by,
                updated_by_name = EXCLUDED.updated_by_name
            RETURNING id
            """;

        long quotaId;
        await using (var command = new NpgsqlCommand(upsertSql, connection, transaction))
        {
            AddQuotaKeyParameters(command, request);
            command.Parameters.AddWithValue("quota_hours", request.QuotaHours);
            command.Parameters.Add(new NpgsqlParameter<string?>("notes", request.Notes?.Trim()));
            command.Parameters.AddWithValue("action_by", request.ActionBy);
            command.Parameters.AddWithValue("action_by_name", request.ActionByName);
            quotaId = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        var action = existingId.HasValue ? "UPDATE" : "CREATE";
        var details = existingId.HasValue
            ? BuildUpdateDetails(oldQuotaHours, request.QuotaHours, oldNotes, request.Notes)
            : $"กำหนดโควต้า {request.QuotaHours:0.##} ชั่วโมง";
        var beforeData = existingId.HasValue
            ? JsonSerializer.Serialize(new { quotaHours = oldQuotaHours, notes = oldNotes })
            : null;
        var afterData = JsonSerializer.Serialize(new
        {
            quotaHours = request.QuotaHours,
            notes = request.Notes
        });

        const string historySql = """
            INSERT INTO public.leave_quota_history
                (leave_quota_id, action, details_text, before_data, after_data,
                 action_by, action_by_name)
            VALUES
                (@quota_id, @action, @details, CAST(@before_data AS jsonb),
                 CAST(@after_data AS jsonb), @action_by, @action_by_name)
            """;
        await using (var command = new NpgsqlCommand(historySql, connection, transaction))
        {
            command.Parameters.AddWithValue("quota_id", quotaId);
            command.Parameters.AddWithValue("action", action);
            command.Parameters.AddWithValue("details", details);
            command.Parameters.Add(new NpgsqlParameter<string?>("before_data", beforeData));
            command.Parameters.AddWithValue("after_data", afterData);
            command.Parameters.AddWithValue("action_by", request.ActionBy);
            command.Parameters.AddWithValue("action_by_name", request.ActionByName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var saved = await FindById(quotaId, cancellationToken);
        return Ok(saved);
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(
        long id,
        [FromQuery] string actionBy,
        [FromQuery] string actionByName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actionBy) || string.IsNullOrWhiteSpace(actionByName))
            return BadRequest("กรุณาระบุผู้ดำเนินการลบโควต้า");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            UPDATE public.leave_quotas
               SET updated_by = @action_by, updated_by_name = @action_by_name
             WHERE id = @id;
            DELETE FROM public.leave_quotas WHERE id = @id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("action_by", actionBy.Trim());
        command.Parameters.AddWithValue("action_by_name", actionByName.Trim());
        var deletedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deletedRows == 0 ? NotFound() : NoContent();
    }

    private async Task<LeaveQuotaDto?> FindById(long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT q.id, q.employee_id, q.leave_type_id, t.name_th, q.quota_year,
                   q.quota_hours, usage.used_hours,
                   q.quota_hours - usage.used_hours,
                   q.notes, q.updated_at
            FROM public.leave_quotas q
            JOIN public.leave_types t ON t.id = q.leave_type_id
            LEFT JOIN LATERAL
            (
                SELECT COALESCE(SUM(d.leave_hours), 0) AS used_hours
                FROM public.leave_documents d
                WHERE d.creator_employee_id = q.employee_id
                  AND d.leave_type_id = q.leave_type_id
                  AND EXTRACT(YEAR FROM d.leave_date)::INT = q.quota_year
                  AND d.status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED')
            ) usage ON TRUE
            WHERE q.id = @id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new LeaveQuotaDto(
            reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2),
            reader.GetString(3), reader.GetInt16(4), reader.GetDecimal(5),
            reader.GetDecimal(6), reader.GetDecimal(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9));
    }

    private static void AddQuotaKeyParameters(NpgsqlCommand command, SaveLeaveQuotaRequest request)
    {
        command.Parameters.AddWithValue("employee_id", request.EmployeeId.Trim());
        command.Parameters.AddWithValue("leave_type_id", request.LeaveTypeId);
        command.Parameters.AddWithValue("quota_year", request.QuotaYear);
    }

    private static string BuildUpdateDetails(
        decimal oldQuotaHours,
        decimal newQuotaHours,
        string? oldNotes,
        string? newNotes)
    {
        var changes = new List<string>();
        if (oldQuotaHours != newQuotaHours)
            changes.Add($"โควต้า {oldQuotaHours:0.##} → {newQuotaHours:0.##} ชั่วโมง");
        if (!string.Equals(oldNotes, newNotes?.Trim(), StringComparison.Ordinal))
            changes.Add("แก้ไขหมายเหตุ");
        return changes.Count == 0 ? "ไม่มีข้อมูลเปลี่ยนแปลง" : string.Join(", ", changes);
    }
}
