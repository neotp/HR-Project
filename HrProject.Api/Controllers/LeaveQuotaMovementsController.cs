using HrProject.Api.Services;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/leave-quota-movements")]
public sealed class LeaveQuotaMovementsController(
    NpgsqlDataSource dataSource,
    PageAccessService pageAccessService) : ControllerBase
{
    [HttpGet("employees")]
    public async Task<ActionResult<IReadOnlyList<LeaveQuotaMovementEmployeeSummaryDto>>> GetEmployees(
        [FromQuery] int year,
        [FromQuery] string actingEmployeeId,
        CancellationToken cancellationToken)
    {
        var accessError = await ValidateAccess(actingEmployeeId, cancellationToken);
        if (accessError is not null) return accessError;
        if (year is < 2000 or > 2200) return BadRequest("ปีโควต้าไม่ถูกต้อง");

        const string sql = """
            WITH employee_years AS
            (
                SELECT employee_id, quota_year FROM public.leave_quotas WHERE quota_year = @year
                UNION
                SELECT employee_id, quota_year FROM public.leave_quota_movements WHERE quota_year = @year
            ),
            employee_types AS
            (
                SELECT employee_id, leave_type_id, quota_year FROM public.leave_quotas WHERE quota_year = @year
                UNION
                SELECT employee_id, leave_type_id, quota_year FROM public.leave_quota_movements WHERE quota_year = @year
            )
            SELECT key.employee_id,
                   COALESCE(NULLIF(b.full_name_th, ''), NULLIF(b.full_name_en, ''),
                            CONCAT_WS(' ', b.first_name_th, b.last_name_th), key.employee_id),
                   COALESCE(c.department, ''), key.quota_year,
                   (SELECT COUNT(*)::INT FROM employee_types type_key
                     WHERE type_key.employee_id = key.employee_id AND type_key.quota_year = key.quota_year),
                   COALESCE(quota.total_hours, 0),
                   COALESCE(usage.used_hours, 0),
                   COALESCE(balance.remaining_hours, 0),
                   last_movement.last_movement_at
            FROM employee_years key
            LEFT JOIN public.employees e ON e.employee_code = key.employee_id
            LEFT JOIN public.employee_basic_info b ON b.employee_id = e.id
            LEFT JOIN public.employee_company_info c ON c.employee_id = e.id
            LEFT JOIN LATERAL
            (
                SELECT SUM(q.quota_hours) AS total_hours
                FROM public.leave_quotas q
                WHERE q.employee_id = key.employee_id AND q.quota_year = key.quota_year
            ) quota ON TRUE
            LEFT JOIN LATERAL
            (
                SELECT COALESCE(SUM(d.leave_hours), 0) AS used_hours
                FROM public.leave_documents d
                WHERE d.creator_employee_id = key.employee_id
                  AND EXTRACT(YEAR FROM d.leave_date)::INT = key.quota_year
                  AND d.status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED')
            ) usage ON TRUE
            LEFT JOIN LATERAL
            (
                SELECT SUM(latest.balance_after) AS remaining_hours
                FROM
                (
                    SELECT DISTINCT ON (m.leave_type_id)
                           SUM(m.change_hours) OVER
                           (PARTITION BY m.leave_type_id ORDER BY m.occurred_at, m.id) AS balance_after
                    FROM public.leave_quota_movements m
                    WHERE m.employee_id = key.employee_id AND m.quota_year = key.quota_year
                    ORDER BY m.leave_type_id, m.occurred_at DESC, m.id DESC
                ) latest
            ) balance ON TRUE
            LEFT JOIN LATERAL
            (
                SELECT MAX(m.occurred_at) AS last_movement_at
                FROM public.leave_quota_movements m
                WHERE m.employee_id = key.employee_id AND m.quota_year = key.quota_year
            ) last_movement ON TRUE
            ORDER BY
                CASE WHEN key.employee_id ~ '^[0-9]+$' THEN key.employee_id::NUMERIC END NULLS LAST,
                key.employee_id
            """;
        var result = new List<LeaveQuotaMovementEmployeeSummaryDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("year", year);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LeaveQuotaMovementEmployeeSummaryDto(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt16(3), reader.GetInt32(4), reader.GetDecimal(5),
                reader.GetDecimal(6), reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8)));
        }
        return Ok(result);
    }

    [HttpGet("employees/{employeeId}/summary")]
    public async Task<ActionResult<IReadOnlyList<LeaveQuotaMovementTypeSummaryDto>>> GetEmployeeSummary(
        string employeeId,
        [FromQuery] int year,
        [FromQuery] string actingEmployeeId,
        CancellationToken cancellationToken)
    {
        var accessError = await ValidateAccess(actingEmployeeId, cancellationToken);
        if (accessError is not null) return accessError;

        const string sql = """
            WITH type_keys AS
            (
                SELECT leave_type_id FROM public.leave_quotas
                 WHERE employee_id = @employee_id AND quota_year = @year
                UNION
                SELECT leave_type_id FROM public.leave_quota_movements
                 WHERE employee_id = @employee_id AND quota_year = @year
            )
            SELECT key.leave_type_id, t.name_th, COALESCE(q.quota_hours, 0),
                   COALESCE(usage.used_hours, 0), COALESCE(balance.remaining_hours, 0)
            FROM type_keys key
            JOIN public.leave_types t ON t.id = key.leave_type_id
            LEFT JOIN public.leave_quotas q ON q.employee_id = @employee_id
                 AND q.quota_year = @year AND q.leave_type_id = key.leave_type_id
            LEFT JOIN LATERAL
            (
                SELECT COALESCE(SUM(d.leave_hours), 0) AS used_hours
                FROM public.leave_documents d
                WHERE d.creator_employee_id = @employee_id
                  AND d.leave_type_id = key.leave_type_id
                  AND EXTRACT(YEAR FROM d.leave_date)::INT = @year
                  AND d.status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED')
            ) usage ON TRUE
            LEFT JOIN LATERAL
            (
                SELECT SUM(m.change_hours) AS remaining_hours
                FROM public.leave_quota_movements m
                WHERE m.employee_id = @employee_id AND m.quota_year = @year
                  AND m.leave_type_id = key.leave_type_id
            ) balance ON TRUE
            ORDER BY t.name_th
            """;
        var result = new List<LeaveQuotaMovementTypeSummaryDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", employeeId.Trim());
        command.Parameters.AddWithValue("year", year);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new LeaveQuotaMovementTypeSummaryDto(
                reader.GetInt64(0), reader.GetString(1), reader.GetDecimal(2),
                reader.GetDecimal(3), reader.GetDecimal(4)));
        return Ok(result);
    }

    [HttpGet("employees/{employeeId}")]
    public async Task<ActionResult<IReadOnlyList<LeaveQuotaMovementDto>>> GetEmployeeMovements(
        string employeeId,
        [FromQuery] int year,
        [FromQuery] string actingEmployeeId,
        [FromQuery] long? leaveTypeId,
        [FromQuery] string? movementType,
        [FromQuery] DateOnly? startDate,
        [FromQuery] DateOnly? endDate,
        [FromQuery] string? reference,
        CancellationToken cancellationToken)
    {
        var accessError = await ValidateAccess(actingEmployeeId, cancellationToken);
        if (accessError is not null) return accessError;

        const string sql = """
            WITH balanced AS
            (
                SELECT m.*,
                       SUM(m.change_hours) OVER
                       (
                           PARTITION BY m.employee_id, m.leave_type_id, m.quota_year
                           ORDER BY m.occurred_at, m.id
                           ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                       ) AS balance_after
                FROM public.leave_quota_movements m
                WHERE m.employee_id = @employee_id AND m.quota_year = @year
            )
            SELECT movement.id, movement.employee_id, movement.leave_type_id,
                   leave_type.name_th, movement.quota_year, movement.movement_type,
                   movement.source_type, movement.source_id, movement.reference_no,
                   GREATEST(movement.change_hours, 0),
                   GREATEST(-movement.change_hours, 0),
                   movement.balance_after - movement.change_hours,
                   movement.balance_after, movement.notes, movement.action_by,
                   movement.action_by_name, movement.occurred_at
            FROM balanced movement
            JOIN public.leave_types leave_type ON leave_type.id = movement.leave_type_id
            WHERE (@leave_type_id IS NULL OR movement.leave_type_id = @leave_type_id)
              AND (@movement_type IS NULL OR movement.movement_type = @movement_type)
              AND (@start_date IS NULL OR movement.occurred_at >= @start_date::DATE)
              AND (@end_date IS NULL OR movement.occurred_at < (@end_date::DATE + INTERVAL '1 day'))
              AND (@reference IS NULL OR movement.reference_no ILIKE '%' || @reference || '%')
            ORDER BY movement.occurred_at DESC, movement.id DESC
            """;
        var result = new List<LeaveQuotaMovementDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", employeeId.Trim());
        command.Parameters.AddWithValue("year", year);
        command.Parameters.Add(new NpgsqlParameter<long?>("leave_type_id", leaveTypeId));
        command.Parameters.Add(new NpgsqlParameter<string?>("movement_type", NullIfWhiteSpace(movementType)));
        command.Parameters.Add(new NpgsqlParameter<DateOnly?>("start_date", startDate));
        command.Parameters.Add(new NpgsqlParameter<DateOnly?>("end_date", endDate));
        command.Parameters.Add(new NpgsqlParameter<string?>("reference", NullIfWhiteSpace(reference)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LeaveQuotaMovementDto(
                reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2),
                reader.GetString(3), reader.GetInt16(4), reader.GetString(5),
                reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetDecimal(9),
                reader.GetDecimal(10), reader.GetDecimal(11), reader.GetDecimal(12),
                reader.IsDBNull(13) ? null : reader.GetString(13), reader.GetString(14),
                reader.GetString(15), reader.GetFieldValue<DateTimeOffset>(16)));
        }
        return Ok(result);
    }

    private async Task<ActionResult?> ValidateAccess(
        string actingEmployeeId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actingEmployeeId)) return BadRequest("กรุณาระบุผู้ใช้งาน");
        var authenticatedEmployeeId = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (!string.Equals(authenticatedEmployeeId, actingEmployeeId, StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับพนักงานที่ขอข้อมูล");
        if (!await pageAccessService.HasAccess(
                actingEmployeeId, "LEAVE_QUOTA_MOVEMENTS", cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์ดู Movement โควต้าวันลา");
        return null;
    }

    private async Task<string?> ResolveAuthenticatedEmployeeId(CancellationToken cancellationToken)
    {
        var tenantId = User.FindFirst("tid")?.Value;
        var objectId = User.FindFirst("oid")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId)) return null;
        const string sql = """
            SELECT employee_id FROM public.microsoft_accounts
            WHERE tenant_id = @tenant_id AND entra_object_id = @object_id AND is_active = TRUE
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("object_id", objectId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
