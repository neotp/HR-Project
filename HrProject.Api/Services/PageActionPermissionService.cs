using Npgsql;

namespace HrProject.Api.Services;

public sealed class PageActionPermissionService(NpgsqlDataSource dataSource)
{
    public async Task<bool> HasPermission(
        string employeeId,
        string pageKey,
        string actionKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(employeeId)) return false;
        const string sql = """
            SELECT
                COALESCE(permission.can_execute, FALSE)
                OR EXISTS
                (
                    SELECT 1
                    FROM public.app_role_members member
                    JOIN public.app_roles role
                      ON role.id = member.app_role_id AND role.is_active = TRUE
                    JOIN public.app_role_page_action_permissions role_permission
                      ON role_permission.app_role_id = role.id
                     AND role_permission.application_page_action_id = action.id
                     AND role_permission.can_execute = TRUE
                    WHERE member.employee_id = @employee_id
                )
            FROM public.application_pages page
            JOIN public.application_page_actions action
              ON action.application_page_id = page.id AND action.is_active = TRUE
            LEFT JOIN public.employee_page_action_permissions permission
              ON permission.application_page_action_id = action.id
             AND permission.employee_id = @employee_id
            WHERE page.page_key = @page_key
              AND action.action_key = @action_key
              AND page.is_active = TRUE
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", employeeId.Trim());
        command.Parameters.AddWithValue("page_key", pageKey);
        command.Parameters.AddWithValue("action_key", actionKey);
        return (bool?)await command.ExecuteScalarAsync(cancellationToken) ?? false;
    }

    public async Task<IReadOnlyList<string>> GetAllowedActions(
        string employeeId,
        string pageKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT action.action_key
            FROM public.application_pages page
            JOIN public.application_page_actions action
              ON action.application_page_id = page.id AND action.is_active = TRUE
            LEFT JOIN public.employee_page_action_permissions permission
              ON permission.application_page_action_id = action.id
             AND permission.employee_id = @employee_id
            WHERE page.page_key = @page_key AND page.is_active = TRUE
              AND
              (
                  COALESCE(permission.can_execute, FALSE)
                  OR EXISTS
                  (
                      SELECT 1
                      FROM public.app_role_members member
                      JOIN public.app_roles role
                        ON role.id = member.app_role_id AND role.is_active = TRUE
                      JOIN public.app_role_page_action_permissions role_permission
                        ON role_permission.app_role_id = role.id
                       AND role_permission.application_page_action_id = action.id
                       AND role_permission.can_execute = TRUE
                      WHERE member.employee_id = @employee_id
                  )
              )
            ORDER BY action.display_order, action.id
            """;
        var result = new List<string>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", employeeId.Trim());
        command.Parameters.AddWithValue("page_key", pageKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }
}
