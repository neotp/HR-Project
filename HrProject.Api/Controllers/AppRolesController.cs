using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/app-roles")]
public sealed class AppRolesController(NpgsqlDataSource dataSource) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AppRoleDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await LoadRoles(cancellationToken));

    [HttpPost]
    public async Task<ActionResult<AppRoleDto>> Create(
        CreateAppRoleRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RoleName))
            return BadRequest("กรุณาระบุชื่อ Role");

        const string sql = """
            INSERT INTO public.app_roles(role_key, role_name, description)
            VALUES (@role_key, @role_name, @description)
            RETURNING id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("role_key", $"ROLE_{Guid.NewGuid():N}".ToUpperInvariant());
        command.Parameters.AddWithValue("role_name", request.RoleName.Trim());
        command.Parameters.Add(new NpgsqlParameter<string?>("description", NullIfEmpty(request.Description)));
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        var role = (await LoadRoles(cancellationToken)).First(item => item.Id == id);
        return CreatedAtAction(nameof(GetAll), role);
    }

    [HttpPut("{roleId:long}/members")]
    public async Task<ActionResult<AppRoleDto>> SaveMembers(
        long roleId,
        SaveAppRoleMembersRequest request,
        CancellationToken cancellationToken)
    {
        var employeeIds = request.EmployeeIds?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var existsCommand = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM public.app_roles WHERE id = @id AND is_active = TRUE)",
            connection, transaction))
        {
            existsCommand.Parameters.AddWithValue("id", roleId);
            if (!((bool?)await existsCommand.ExecuteScalarAsync(cancellationToken) ?? false))
                return NotFound("ไม่พบ Role");
        }

        await using (var deleteCommand = new NpgsqlCommand(
            "DELETE FROM public.app_role_members WHERE app_role_id = @role_id", connection, transaction))
        {
            deleteCommand.Parameters.AddWithValue("role_id", roleId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var employeeId in employeeIds)
        {
            await using var insertCommand = new NpgsqlCommand(
                "INSERT INTO public.app_role_members(app_role_id, employee_id) VALUES (@role_id, @employee_id)",
                connection, transaction);
            insertCommand.Parameters.AddWithValue("role_id", roleId);
            insertCommand.Parameters.AddWithValue("employee_id", employeeId);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return Ok((await LoadRoles(cancellationToken)).First(item => item.Id == roleId));
    }

    [HttpGet("{roleId:long}/permissions")]
    public async Task<ActionResult<IReadOnlyList<PagePermissionDto>>> GetPermissions(
        long roleId, CancellationToken cancellationToken) =>
        Ok(await LoadPermissions(roleId, cancellationToken));

    [HttpPut("{roleId:long}/permissions")]
    public async Task<ActionResult<IReadOnlyList<PagePermissionDto>>> SavePermissions(
        long roleId,
        SaveRolePagePermissionsRequest request,
        CancellationToken cancellationToken)
    {
        var permissions = request.Permissions?.GroupBy(item => item.PageId).Select(group => group.Last()).ToList() ?? [];
        if (roleId <= 0 || permissions.Count == 0 || permissions.Any(item => item.PageId <= 0) ||
            string.IsNullOrWhiteSpace(request.UpdatedBy) || string.IsNullOrWhiteSpace(request.UpdatedByName))
            return BadRequest("ข้อมูลสิทธิ์ Role ไม่ครบถ้วน");

        const string sql = """
            INSERT INTO public.app_role_page_permissions
                (app_role_id, application_page_id, can_access, updated_by, updated_by_name)
            SELECT @role_id, page.id, @can_access, @updated_by, @updated_by_name
            FROM public.application_pages page
            WHERE page.id = @page_id AND page.is_active = TRUE
            ON CONFLICT (app_role_id, application_page_id)
            DO UPDATE SET can_access = EXCLUDED.can_access,
                          updated_by = EXCLUDED.updated_by,
                          updated_by_name = EXCLUDED.updated_by_name
            """;
        await SavePermissionRows(roleId, permissions, request.UpdatedBy, request.UpdatedByName, sql, cancellationToken);
        return Ok(await LoadPermissions(roleId, cancellationToken));
    }

    [HttpGet("{roleId:long}/actions")]
    public async Task<ActionResult<IReadOnlyList<PageActionPermissionDto>>> GetActions(
        long roleId, CancellationToken cancellationToken) =>
        Ok(await LoadActions(roleId, cancellationToken));

    [HttpPut("{roleId:long}/actions")]
    public async Task<ActionResult<IReadOnlyList<PageActionPermissionDto>>> SaveActions(
        long roleId,
        SaveRolePageActionPermissionsRequest request,
        CancellationToken cancellationToken)
    {
        var permissions = request.Permissions?.GroupBy(item => item.ActionId).Select(group => group.Last()).ToList() ?? [];
        if (roleId <= 0 || permissions.Count == 0 || permissions.Any(item => item.ActionId <= 0) ||
            string.IsNullOrWhiteSpace(request.UpdatedBy) || string.IsNullOrWhiteSpace(request.UpdatedByName))
            return BadRequest("ข้อมูลสิทธิ์เพิ่มเติมของ Role ไม่ครบถ้วน");

        const string sql = """
            INSERT INTO public.app_role_page_action_permissions
                (app_role_id, application_page_action_id, can_execute, updated_by, updated_by_name)
            SELECT @role_id, action.id, @can_execute, @updated_by, @updated_by_name
            FROM public.application_page_actions action
            JOIN public.application_pages page ON page.id = action.application_page_id
            WHERE action.id = @action_id AND action.is_active = TRUE AND page.is_active = TRUE
            ON CONFLICT (app_role_id, application_page_action_id)
            DO UPDATE SET can_execute = EXCLUDED.can_execute,
                          updated_by = EXCLUDED.updated_by,
                          updated_by_name = EXCLUDED.updated_by_name
            """;
        await SaveActionRows(roleId, permissions, request.UpdatedBy, request.UpdatedByName, sql, cancellationToken);
        return Ok(await LoadActions(roleId, cancellationToken));
    }

    private async Task<IReadOnlyList<AppRoleDto>> LoadRoles(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT role.id, role.role_key, role.role_name, role.description, role.is_active,
                   COALESCE(ARRAY_AGG(member.employee_id ORDER BY member.employee_id)
                            FILTER (WHERE member.employee_id IS NOT NULL), ARRAY[]::VARCHAR[]) members
            FROM public.app_roles role
            LEFT JOIN public.app_role_members member ON member.app_role_id = role.id
            WHERE role.is_active = TRUE
            GROUP BY role.id
            ORDER BY role.role_name, role.id
            """;
        var result = new List<AppRoleDto>();
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new AppRoleDto(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4),
                reader.GetFieldValue<string[]>(5)));
        return result;
    }

    private async Task<IReadOnlyList<PagePermissionDto>> LoadPermissions(long roleId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT page.id, page.page_key, page.page_name, page.route_path,
                   page.category_name, page.display_order, COALESCE(permission.can_access, FALSE)
            FROM public.application_pages page
            LEFT JOIN public.app_role_page_permissions permission
              ON permission.application_page_id = page.id AND permission.app_role_id = @role_id
            WHERE page.is_active = TRUE
            ORDER BY page.display_order, page.id
            """;
        var result = new List<PagePermissionDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("role_id", roleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new PagePermissionDto(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetBoolean(6)));
        return result;
    }

    private async Task<IReadOnlyList<PageActionPermissionDto>> LoadActions(long roleId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT action.id, page.id, page.page_key, page.page_name, page.category_name,
                   action.action_key, action.action_name, action.description, action.display_order,
                   COALESCE(permission.can_execute, FALSE)
            FROM public.application_pages page
            JOIN public.application_page_actions action
              ON action.application_page_id = page.id AND action.is_active = TRUE
            LEFT JOIN public.app_role_page_action_permissions permission
              ON permission.application_page_action_id = action.id AND permission.app_role_id = @role_id
            WHERE page.is_active = TRUE
            ORDER BY page.display_order, page.id, action.display_order, action.id
            """;
        var result = new List<PageActionPermissionDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("role_id", roleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new PageActionPermissionDto(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8), reader.GetBoolean(9)));
        return result;
    }

    private async Task SavePermissionRows(long roleId, IReadOnlyList<SavePagePermissionItem> permissions,
        string updatedBy, string updatedByName, string sql, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var permission in permissions)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("role_id", roleId);
            command.Parameters.AddWithValue("page_id", permission.PageId);
            command.Parameters.AddWithValue("can_access", permission.CanAccess);
            command.Parameters.AddWithValue("updated_by", updatedBy.Trim());
            command.Parameters.AddWithValue("updated_by_name", updatedByName.Trim());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SaveActionRows(long roleId, IReadOnlyList<SavePageActionPermissionItem> permissions,
        string updatedBy, string updatedByName, string sql, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var permission in permissions)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("role_id", roleId);
            command.Parameters.AddWithValue("action_id", permission.ActionId);
            command.Parameters.AddWithValue("can_execute", permission.CanExecute);
            command.Parameters.AddWithValue("updated_by", updatedBy.Trim());
            command.Parameters.AddWithValue("updated_by_name", updatedByName.Trim());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
