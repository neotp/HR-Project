using HrProject.Api.Services;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/leave-quota-annual-reports")]
[Authorize(Policy = "HrApiScope")]
public sealed class LeaveQuotaAnnualReportsController(
    NpgsqlDataSource dataSource,
    PageAccessService pageAccessService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LeaveQuotaAnnualReportDto>>> GetAll(
        [FromQuery] int year,
        CancellationToken cancellationToken)
    {
        if (year is < 2000 or > 2200)
            return BadRequest("ปีโควต้าไม่ถูกต้อง");

        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actor) ||
            !await pageAccessService.HasAccess(actor, "LEAVE_QUOTA_ANNUAL_REPORT", cancellationToken))
            return Forbid();

        const string sql = """
            WITH report_keys AS
            (
                SELECT employee_id, leave_type_id, quota_year
                FROM public.leave_quota_yearly_rollovers
                WHERE quota_year = @year
                UNION
                SELECT employee_id, leave_type_id, quota_year
                FROM public.leave_quota_excess_details
                WHERE quota_year = @year
                UNION
                SELECT employee_id, leave_type_id, quota_year
                FROM public.leave_quotas
                WHERE quota_year = @year
            ),
            excess AS
            (
                SELECT employee_id, leave_type_id, quota_year,
                       SUM(credited_hours) AS credited_hours,
                       SUM(excess_hours) AS excess_hours
                FROM public.leave_quota_excess_details
                WHERE quota_year = @year
                GROUP BY employee_id, leave_type_id, quota_year
            )
            SELECT key.employee_id,
                   COALESCE(NULLIF(basic.full_name_th, ''), NULLIF(basic.full_name_en, ''),
                            CONCAT_WS(' ', basic.first_name_th, basic.last_name_th), key.employee_id),
                   COALESCE(company.department, ''),
                   key.leave_type_id, leave_type.name_th, key.quota_year,
                   COALESCE(rollover.previous_remaining_hours, 0),
                   COALESCE(
                       quota.quota_hours,
                       COALESCE(rollover.new_default_hours, 0) + COALESCE(excess.credited_hours, 0),
                       0),
                   GREATEST(COALESCE(quota.quota_hours, 0) - COALESCE(usage.used_hours, 0), 0),
                   COALESCE(quota.annual_excess_hours, rollover.annual_excess_hours, 0)
                       + COALESCE(excess.excess_hours, 0),
                   rollover.processed_at
            FROM report_keys key
            JOIN public.leave_types leave_type ON leave_type.id = key.leave_type_id
            LEFT JOIN public.employees employee ON employee.employee_code = key.employee_id
            LEFT JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
            LEFT JOIN public.employee_company_info company ON company.employee_id = employee.id
            LEFT JOIN public.leave_quota_yearly_rollovers rollover
              ON rollover.employee_id = key.employee_id
             AND rollover.leave_type_id = key.leave_type_id
             AND rollover.quota_year = key.quota_year
            LEFT JOIN excess
              ON excess.employee_id = key.employee_id
             AND excess.leave_type_id = key.leave_type_id
             AND excess.quota_year = key.quota_year
            LEFT JOIN public.leave_quotas quota
              ON quota.employee_id = key.employee_id
             AND quota.leave_type_id = key.leave_type_id
             AND quota.quota_year = key.quota_year
            LEFT JOIN LATERAL
            (
                SELECT COALESCE(SUM(allocation.allocated_hours), 0) AS used_hours
                FROM public.leave_document_quota_allocations allocation
                WHERE allocation.employee_id = key.employee_id
                  AND allocation.leave_type_id = key.leave_type_id
                  AND (allocation.source_quota_year = key.quota_year OR allocation.leave_year = key.quota_year)
                  AND allocation.released_at IS NULL
            ) usage ON TRUE
            ORDER BY
                CASE WHEN key.employee_id ~ '^[0-9]+$' THEN key.employee_id::NUMERIC END NULLS LAST,
                key.employee_id, leave_type.name_th
            """;

        var result = new List<LeaveQuotaAnnualReportDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("year", year);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LeaveQuotaAnnualReportDto(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetString(4), reader.GetInt16(5),
                reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8),
                reader.GetDecimal(9),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10)));
        }

        return Ok(result);
    }

    private async Task<string?> ResolveAuthenticatedEmployeeId(CancellationToken cancellationToken)
    {
        var directEmployeeId = User.FindFirst("employee_id")?.Value;
        if (!string.IsNullOrWhiteSpace(directEmployeeId))
            return directEmployeeId.Trim();

        var tenantId = User.FindFirst("tid")?.Value;
        var objectId = User.FindFirst("oid")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId))
            return null;

        const string sql = """
            SELECT employee_id
            FROM public.microsoft_accounts
            WHERE tenant_id = @tenant_id AND entra_object_id = @object_id AND is_active = TRUE
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("object_id", objectId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }
}
