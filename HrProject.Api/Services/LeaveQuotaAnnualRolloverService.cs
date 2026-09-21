using Npgsql;

namespace HrProject.Api.Services;

public sealed class LeaveQuotaAnnualRolloverService(NpgsqlDataSource dataSource)
{
    public async Task<int> ProcessAsync(int quotaYear, CancellationToken cancellationToken)
    {
        if (quotaYear is < 2000 or > 2200)
            throw new ArgumentOutOfRangeException(nameof(quotaYear));

        var processDate = new DateOnly(quotaYear, 1, 1);
        const string sql = """
            WITH annual_lock AS
            (
                SELECT pg_advisory_xact_lock(hashtextextended('LEAVE_QUOTA_ANNUAL:' || @quota_year, 0))
            ),
            candidates AS
            (
                SELECT employee.employee_code AS employee_id,
                       leave_type.id AS leave_type_id,
                       leave_type.default_hours AS new_default_hours,
                       GREATEST(
                           COALESCE(previous_quota.quota_hours, 0) - COALESCE(previous_usage.used_hours, 0),
                           0
                       ) AS previous_remaining_hours
                FROM public.employees employee
                JOIN public.employee_company_info company ON company.employee_id = employee.id
                CROSS JOIN public.leave_types leave_type
                CROSS JOIN annual_lock
                LEFT JOIN public.leave_quotas previous_quota
                  ON previous_quota.employee_id = employee.employee_code
                 AND previous_quota.leave_type_id = leave_type.id
                 AND previous_quota.quota_year = @quota_year - 1
                LEFT JOIN LATERAL
                (
                    SELECT COALESCE(SUM(document.leave_hours), 0) AS used_hours
                    FROM public.leave_documents document
                    WHERE document.creator_employee_id = employee.employee_code
                      AND document.leave_type_id = leave_type.id
                      AND EXTRACT(YEAR FROM document.leave_date)::INT = @quota_year - 1
                      AND document.status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED')
                ) previous_usage ON TRUE
                WHERE employee.is_active = TRUE
                  AND leave_type.is_active = TRUE
                  AND company.start_date IS NOT NULL
                  AND company.start_date < (@process_date::DATE - INTERVAL '2 years')::DATE
            ),
            inserted_rollovers AS
            (
                INSERT INTO public.leave_quota_yearly_rollovers
                    (employee_id, leave_type_id, quota_year,
                     previous_remaining_hours, new_default_hours,
                     opening_quota_hours, annual_excess_hours)
                SELECT employee_id, leave_type_id, @quota_year,
                       previous_remaining_hours, new_default_hours,
                       new_default_hours, previous_remaining_hours
                FROM candidates
                ON CONFLICT (employee_id, leave_type_id, quota_year) DO NOTHING
                RETURNING employee_id, leave_type_id, new_default_hours
            ),
            upserted_quotas AS
            (
                INSERT INTO public.leave_quotas
                    (employee_id, leave_type_id, quota_year, quota_hours, notes,
                     created_by, created_by_name, updated_by, updated_by_name)
                SELECT employee_id, leave_type_id, @quota_year, new_default_hours,
                       @notes, 'SYSTEM', @system_name, 'SYSTEM', @system_name
                FROM inserted_rollovers
                ON CONFLICT (employee_id, leave_type_id, quota_year)
                DO UPDATE SET
                    quota_hours = GREATEST(public.leave_quotas.quota_hours, EXCLUDED.quota_hours),
                    notes = EXCLUDED.notes,
                    updated_by = EXCLUDED.updated_by,
                    updated_by_name = EXCLUDED.updated_by_name
                RETURNING id
            )
            SELECT COUNT(*)::INT FROM inserted_rollovers
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("quota_year", quotaYear);
        command.Parameters.AddWithValue("process_date", processDate);
        command.Parameters.AddWithValue("notes", $"เติมโควต้าประจำปี {quotaYear} ตามค่าเริ่มต้น");
        command.Parameters.AddWithValue("system_name", "ระบบเติมโควต้าประจำปี");
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }
}
