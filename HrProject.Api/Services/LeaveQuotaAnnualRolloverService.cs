using Npgsql;

namespace HrProject.Api.Services;

public sealed class LeaveQuotaAnnualRolloverService(NpgsqlDataSource dataSource)
{
    public async Task<int> ProcessAsync(int quotaYear, CancellationToken cancellationToken)
    {
        if (quotaYear is < 2000 or > 2200)
            throw new ArgumentOutOfRangeException(nameof(quotaYear));

        const string sql = """
            WITH annual_lock AS
            (
                SELECT pg_advisory_xact_lock(hashtextextended('LEAVE_QUOTA_ANNUAL:' || @quota_year, 0))
            ),
            candidates AS
            (
                SELECT employee.employee_code AS employee_id,
                       leave_type.id AS leave_type_id,
                       leave_type.code AS leave_type_code,
                       CASE WHEN leave_type.code = 'VACATION'
                            THEN public.calculate_vacation_entitlement_hours(employee.employee_code, @quota_year)
                            ELSE leave_type.default_hours END AS new_entitlement_hours,
                       GREATEST(COALESCE(previous_quota.quota_hours, 0)
                           - COALESCE(previous_usage.used_hours, 0), 0) AS previous_remaining_hours,
                       COALESCE(future_carry_usage.reserved_hours, 0) AS future_carry_reserved_hours
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
                    SELECT COALESCE(SUM(allocation.allocated_hours), 0) AS used_hours
                    FROM public.leave_document_quota_allocations allocation
                    WHERE allocation.employee_id = employee.employee_code
                      AND allocation.leave_type_id = leave_type.id
                      AND allocation.source_quota_year = @quota_year - 1
                      AND allocation.released_at IS NULL
                ) previous_usage ON TRUE
                LEFT JOIN LATERAL
                (
                    SELECT COALESCE(SUM(allocation.allocated_hours), 0) AS reserved_hours
                    FROM public.leave_document_quota_allocations allocation
                    WHERE allocation.employee_id = employee.employee_code
                      AND allocation.leave_type_id = leave_type.id
                      AND allocation.source_quota_year = @quota_year - 1
                      AND allocation.leave_year = @quota_year
                      AND allocation.allocation_type = 'PRIOR_YEAR_BALANCE'
                      AND allocation.released_at IS NULL
                ) future_carry_usage ON TRUE
                WHERE employee.is_active = TRUE
                  AND leave_type.is_active = TRUE
                  AND company.start_date IS NOT NULL
                  AND company.start_date <= make_date(@quota_year, 12, 31)
            ),
            calculated AS
            (
                SELECT *,
                       CASE WHEN leave_type_code = 'VACATION'
                            THEN LEAST(96, previous_remaining_hours + future_carry_reserved_hours
                                 + new_entitlement_hours)
                            ELSE new_entitlement_hours END AS opening_quota_hours,
                       CASE WHEN leave_type_code = 'VACATION'
                            THEN GREATEST(previous_remaining_hours + future_carry_reserved_hours
                                 + new_entitlement_hours - 96, 0)
                            ELSE 0 END AS annual_excess_hours,
                       CASE WHEN leave_type_code = 'VACATION'
                            THEN LEAST(previous_remaining_hours + future_carry_reserved_hours,
                                 GREATEST(96 - new_entitlement_hours, 0))
                            ELSE 0 END AS carried_forward_hours
                FROM candidates
            ),
            inserted_rollovers AS
            (
                INSERT INTO public.leave_quota_yearly_rollovers
                    (employee_id, leave_type_id, quota_year, previous_remaining_hours,
                     new_default_hours, opening_quota_hours, annual_excess_hours)
                SELECT employee_id, leave_type_id, @quota_year, previous_remaining_hours,
                       new_entitlement_hours, opening_quota_hours, annual_excess_hours
                FROM calculated
                ON CONFLICT (employee_id, leave_type_id, quota_year) DO NOTHING
                RETURNING employee_id, leave_type_id
            ),
            rows_to_finalize AS
            (
                SELECT calculated.*
                FROM calculated
                JOIN inserted_rollovers USING (employee_id, leave_type_id)
            ),
            upserted_quotas AS
            (
                INSERT INTO public.leave_quotas
                    (employee_id, leave_type_id, quota_year, quota_hours, notes,
                     created_by, created_by_name, updated_by, updated_by_name,
                     quota_status, new_entitlement_hours, carried_forward_hours,
                     annual_excess_hours, finalized_at)
                SELECT employee_id, leave_type_id, @quota_year, opening_quota_hours,
                       @notes, 'SYSTEM', @system_name, 'SYSTEM', @system_name,
                       'FINALIZED', new_entitlement_hours, carried_forward_hours,
                       annual_excess_hours, CURRENT_TIMESTAMP
                FROM rows_to_finalize
                ON CONFLICT (employee_id, leave_type_id, quota_year)
                DO UPDATE SET
                    quota_hours = EXCLUDED.quota_hours,
                    notes = EXCLUDED.notes,
                    updated_by = EXCLUDED.updated_by,
                    updated_by_name = EXCLUDED.updated_by_name,
                    quota_status = 'FINALIZED',
                    new_entitlement_hours = EXCLUDED.new_entitlement_hours,
                    carried_forward_hours = EXCLUDED.carried_forward_hours,
                    annual_excess_hours = EXCLUDED.annual_excess_hours,
                    finalized_at = CURRENT_TIMESTAMP
                RETURNING id
            )
            SELECT COUNT(*)::INT FROM inserted_rollovers
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.CommandTimeout = 300;
        command.Parameters.AddWithValue("quota_year", quotaYear);
        command.Parameters.AddWithValue("notes", $"Annual quota calculation for {quotaYear}");
        command.Parameters.AddWithValue("system_name", "Annual leave quota system");
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }
}
