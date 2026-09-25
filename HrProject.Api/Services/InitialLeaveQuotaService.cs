using Npgsql;

namespace HrProject.Api.Services;

public static class InitialLeaveQuotaService
{
    public static async Task CreateForNewEmployee(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string employeeCode,
        DateOnly startDate,
        string actionBy,
        string actionByName,
        CancellationToken cancellationToken)
    {
        var quotaYear = startDate == default
            ? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
                DateTimeOffset.UtcNow,
                OperatingSystem.IsWindows() ? "SE Asia Standard Time" : "Asia/Bangkok").Year
            : startDate.Year;

        const string sql = """
            WITH inserted_quotas AS
            (
                INSERT INTO public.leave_quotas
                    (employee_id, leave_type_id, quota_year, quota_hours, used_hours,
                     notes, created_by, created_by_name, updated_by, updated_by_name,
                     quota_status, new_entitlement_hours, carried_forward_hours,
                     annual_excess_hours, finalized_at)
                SELECT @employee_code, leave_type.id, @quota_year,
                       CASE WHEN leave_type.code = 'VACATION' THEN 24
                            ELSE leave_type.default_hours END,
                       0,
                       CASE WHEN leave_type.code = 'VACATION'
                            THEN 'Initial vacation entitlement: 3 days'
                            ELSE 'Initial quota from leave type master' END,
                       @action_by, @action_by_name, @action_by, @action_by_name,
                       'FINALIZED',
                       CASE WHEN leave_type.code = 'VACATION' THEN 24
                            ELSE leave_type.default_hours END,
                       0, 0, CURRENT_TIMESTAMP
                FROM public.leave_types leave_type
                WHERE leave_type.is_active = TRUE
                ON CONFLICT (employee_id, leave_type_id, quota_year) DO NOTHING
                RETURNING id, quota_hours, notes
            )
            INSERT INTO public.leave_quota_history
                (leave_quota_id, action, details_text, before_data, after_data,
                 action_by, action_by_name)
            SELECT inserted.id, 'CREATE', inserted.notes, NULL,
                   jsonb_build_object(
                       'quotaHours', inserted.quota_hours,
                       'quotaYear', @quota_year,
                       'source', 'PRE_EMPLOYEE_CONVERSION'),
                   @action_by, @action_by_name
            FROM inserted_quotas inserted
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("employee_code", employeeCode);
        command.Parameters.AddWithValue("quota_year", quotaYear);
        command.Parameters.AddWithValue("action_by", actionBy);
        command.Parameters.AddWithValue("action_by_name", actionByName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
