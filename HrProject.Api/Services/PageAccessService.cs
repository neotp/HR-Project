using Npgsql;

namespace HrProject.Api.Services;

public sealed class PageAccessService(NpgsqlDataSource dataSource)
{
    public async Task<PageAccessResult> GetAccess(
        string employeeId,
        string pageKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || string.IsNullOrWhiteSpace(pageKey))
            return new PageAccessResult(false, false, false);

        const string sql = """
            WITH actor AS
            (
                SELECT e.id, e.employee_code,
                       ARRAY_REMOVE(ARRAY[
                           NULLIF(REGEXP_REPLACE(UPPER(BTRIM(COALESCE(b.full_name_th, ''))), '\s+', ' ', 'g'), ''),
                           NULLIF(REGEXP_REPLACE(UPPER(BTRIM(COALESCE(b.full_name_en, ''))), '\s+', ' ', 'g'), ''),
                           NULLIF(REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', b.first_name_th, b.last_name_th))), '\s+', ' ', 'g'), ''),
                           NULLIF(REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', b.first_name_en, b.last_name_en))), '\s+', ' ', 'g'), '')
                       ], NULL) AS names
                FROM public.employees e
                JOIN public.employee_basic_info b ON b.employee_id = e.id
                WHERE e.employee_code = @employee_id AND e.is_active = TRUE
                LIMIT 1
            )
            SELECT page.is_enabled,
                   COALESCE(direct_permission.can_access, FALSE)
                   OR EXISTS
                   (
                       SELECT 1
                       FROM public.app_role_members member
                       JOIN public.app_roles role
                         ON role.id = member.app_role_id AND role.is_active = TRUE
                       JOIN public.app_role_page_permissions role_permission
                         ON role_permission.app_role_id = role.id
                        AND role_permission.application_page_id = page.id
                        AND role_permission.can_access = TRUE
                       WHERE member.employee_id = @employee_id
                   ) AS explicit_access,
                   CASE WHEN page.page_key = 'LEAVE_PENDING' THEN EXISTS
                   (
                       SELECT 1
                       FROM public.employees subordinate
                       JOIN public.employee_company_info company
                         ON company.employee_id = subordinate.id
                       CROSS JOIN actor
                       WHERE subordinate.is_active = TRUE
                         AND subordinate.id <> actor.id
                         AND
                         (
                             REGEXP_REPLACE(UPPER(BTRIM(COALESCE(company.supervisor_name, ''))), '\s+', ' ', 'g') = ANY(actor.names)
                             OR REGEXP_REPLACE(UPPER(BTRIM(COALESCE(company.leave_approver_name, ''))), '\s+', ' ', 'g') = ANY(actor.names)
                             OR UPPER(BTRIM(COALESCE(company.supervisor_name, ''))) = UPPER(actor.employee_code)
                             OR UPPER(BTRIM(COALESCE(company.leave_approver_name, ''))) = UPPER(actor.employee_code)
                         )
                   ) ELSE FALSE END AS business_access
            FROM public.application_pages page
            LEFT JOIN public.employee_page_permissions direct_permission
              ON direct_permission.application_page_id = page.id
             AND direct_permission.employee_id = @employee_id
            WHERE page.page_key = @page_key AND page.is_active = TRUE
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", employeeId.Trim());
        command.Parameters.AddWithValue("page_key", pageKey.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new PageAccessResult(false, false, false);

        var isEnabled = reader.GetBoolean(0);
        var explicitAccess = reader.GetBoolean(1);
        var businessAccess = reader.GetBoolean(2);
        return new PageAccessResult(
            isEnabled && (explicitAccess || businessAccess),
            businessAccess,
            explicitAccess);
    }

    public async Task<bool> HasAccess(
        string employeeId,
        string pageKey,
        CancellationToken cancellationToken) =>
        (await GetAccess(employeeId, pageKey, cancellationToken)).CanAccess;

    public sealed record PageAccessResult(
        bool CanAccess,
        bool GrantedByBusinessRule,
        bool GrantedByExplicitPermission);
}
