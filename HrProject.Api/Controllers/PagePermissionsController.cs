using HrProject.Shared.Models;
using HrProject.Api.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/page-permissions")]
public sealed class PagePermissionsController(
    NpgsqlDataSource dataSource,
    PageActionPermissionService actionPermissionService,
    PageAccessService pageAccessService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PagePermissionDto>>> GetAll(
        [FromQuery] string employeeId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return BadRequest("กรุณาระบุพนักงาน");

        return Ok(await LoadPermissions(employeeId.Trim(), cancellationToken));
    }

    [HttpPut("employees/{employeeId}")]
    public async Task<ActionResult<IReadOnlyList<PagePermissionDto>>> Save(
        string employeeId,
        SaveEmployeePagePermissionsRequest request,
        CancellationToken cancellationToken)
    {
        var permissions = request.Permissions?
            .GroupBy(item => item.PageId)
            .Select(group => group.Last())
            .ToList() ?? [];

        if (string.IsNullOrWhiteSpace(employeeId) ||
            !string.Equals(employeeId.Trim(), request.EmployeeId?.Trim(), StringComparison.OrdinalIgnoreCase) ||
            permissions.Count == 0 ||
            permissions.Any(item => item.PageId <= 0) ||
            string.IsNullOrWhiteSpace(request.UpdatedBy) ||
            string.IsNullOrWhiteSpace(request.UpdatedByName))
        {
            return BadRequest("ข้อมูลสิทธิ์การเข้าถึงไม่ครบถ้วน");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string sql = """
            INSERT INTO public.employee_page_permissions
                (employee_id, application_page_id, can_access,
                 updated_by, updated_by_name)
            SELECT @employee_id, p.id, @can_access,
                   @updated_by, @updated_by_name
            FROM public.application_pages p
            WHERE p.id = @page_id
              AND p.is_active = TRUE
            ON CONFLICT (employee_id, application_page_id)
            DO UPDATE SET
                can_access = EXCLUDED.can_access,
                updated_by = EXCLUDED.updated_by,
                updated_by_name = EXCLUDED.updated_by_name
            """;

        foreach (var permission in permissions)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("employee_id", employeeId.Trim());
            command.Parameters.AddWithValue("page_id", permission.PageId);
            command.Parameters.AddWithValue("can_access", permission.CanAccess);
            command.Parameters.AddWithValue("updated_by", request.UpdatedBy.Trim());
            command.Parameters.AddWithValue("updated_by_name", request.UpdatedByName.Trim());
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return BadRequest($"ไม่พบหน้าระบบรหัส {permission.PageId}");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return Ok(await LoadPermissions(employeeId.Trim(), cancellationToken));
    }

    [HttpGet("actions")]
    public async Task<ActionResult<IReadOnlyList<PageActionPermissionDto>>> GetActions(
        [FromQuery] string employeeId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(employeeId)) return BadRequest("กรุณาระบุพนักงาน");
        return Ok(await LoadActionPermissions(employeeId.Trim(), cancellationToken));
    }

    [HttpGet("current-actions")]
    public async Task<ActionResult<CurrentPageActionPermissionsDto>> GetCurrentActions(
        [FromQuery] string employeeId,
        [FromQuery] string pageKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || string.IsNullOrWhiteSpace(pageKey))
            return BadRequest("กรุณาระบุพนักงานและหน้าระบบ");
        var actions = await actionPermissionService.GetAllowedActions(
            employeeId.Trim(), pageKey.Trim().ToUpperInvariant(), cancellationToken);
        return Ok(new CurrentPageActionPermissionsDto(pageKey.Trim().ToUpperInvariant(), actions));
    }

    [HttpGet("current-access/{pageKey}")]
    public async Task<ActionResult<CurrentPageAccessDto>> GetCurrentAccess(
        string pageKey,
        CancellationToken cancellationToken)
    {
        var employeeId = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(employeeId))
            return StatusCode(StatusCodes.Status403Forbidden,
                "ไม่พบบัญชีพนักงานที่เชื่อมกับ Microsoft");

        var normalizedPageKey = pageKey.Trim().ToUpperInvariant();
        var access = await pageAccessService.GetAccess(
            employeeId, normalizedPageKey, cancellationToken);
        return Ok(new CurrentPageAccessDto(
            normalizedPageKey,
            access.CanAccess,
            access.GrantedByBusinessRule,
            access.GrantedByExplicitPermission));
    }

    [HttpGet("availability")]
    public async Task<ActionResult<IReadOnlyList<ApplicationPageAvailabilityDto>>> GetAvailability(
        CancellationToken cancellationToken) =>
        Ok(await LoadAvailability(cancellationToken));

    [HttpPut("availability")]
    public async Task<ActionResult<IReadOnlyList<ApplicationPageAvailabilityDto>>> SaveAvailability(
        SaveApplicationPageAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        var pages = request.Pages?
            .GroupBy(item => item.PageId)
            .Select(group => group.Last())
            .ToList() ?? [];

        if (pages.Count == 0 || pages.Any(item => item.PageId <= 0) ||
            string.IsNullOrWhiteSpace(request.UpdatedBy) ||
            string.IsNullOrWhiteSpace(request.UpdatedByName))
            return BadRequest("ข้อมูลสถานะเมนูไม่ครบถ้วน");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            UPDATE public.application_pages
            SET is_enabled = @is_enabled,
                availability_updated_by = @updated_by,
                availability_updated_by_name = @updated_by_name,
                availability_updated_at = CURRENT_TIMESTAMP
            WHERE id = @page_id
              AND is_active = TRUE
              AND page_key <> 'PERMISSIONS'
            """;

        foreach (var page in pages)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("page_id", page.PageId);
            command.Parameters.AddWithValue("is_enabled", page.IsEnabled);
            command.Parameters.AddWithValue("updated_by", request.UpdatedBy.Trim());
            command.Parameters.AddWithValue("updated_by_name", request.UpdatedByName.Trim());
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                const string pageKeySql = "SELECT page_key FROM public.application_pages WHERE id = @page_id AND is_active = TRUE";
                await using var keyCommand = new NpgsqlCommand(pageKeySql, connection, transaction);
                keyCommand.Parameters.AddWithValue("page_id", page.PageId);
                var pageKey = await keyCommand.ExecuteScalarAsync(cancellationToken) as string;
                if (!string.Equals(pageKey, "PERMISSIONS", StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return BadRequest($"ไม่พบหน้าระบบรหัส {page.PageId}");
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return Ok(await LoadAvailability(cancellationToken));
    }

    [HttpPut("employees/{employeeId}/actions")]
    public async Task<ActionResult<IReadOnlyList<PageActionPermissionDto>>> SaveActions(
        string employeeId,
        SaveEmployeePageActionPermissionsRequest request,
        CancellationToken cancellationToken)
    {
        var permissions = request.Permissions?
            .GroupBy(item => item.ActionId)
            .Select(group => group.Last())
            .ToList() ?? [];
        if (string.IsNullOrWhiteSpace(employeeId) ||
            !string.Equals(employeeId.Trim(), request.EmployeeId?.Trim(), StringComparison.OrdinalIgnoreCase) ||
            permissions.Count == 0 || permissions.Any(item => item.ActionId <= 0) ||
            string.IsNullOrWhiteSpace(request.UpdatedBy) || string.IsNullOrWhiteSpace(request.UpdatedByName))
            return BadRequest("ข้อมูลสิทธิ์เพิ่มเติมไม่ครบถ้วน");

        const string sql = """
            INSERT INTO public.employee_page_action_permissions
                (employee_id, application_page_action_id, can_execute,
                 updated_by, updated_by_name)
            SELECT @employee_id, action.id, @can_execute,
                   @updated_by, @updated_by_name
            FROM public.application_page_actions action
            JOIN public.application_pages page ON page.id = action.application_page_id
            WHERE action.id = @action_id AND action.is_active = TRUE AND page.is_active = TRUE
            ON CONFLICT (employee_id, application_page_action_id)
            DO UPDATE SET can_execute = EXCLUDED.can_execute,
                          updated_by = EXCLUDED.updated_by,
                          updated_by_name = EXCLUDED.updated_by_name
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var permission in permissions)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("employee_id", employeeId.Trim());
            command.Parameters.AddWithValue("action_id", permission.ActionId);
            command.Parameters.AddWithValue("can_execute", permission.CanExecute);
            command.Parameters.AddWithValue("updated_by", request.UpdatedBy.Trim());
            command.Parameters.AddWithValue("updated_by_name", request.UpdatedByName.Trim());
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return BadRequest($"ไม่พบสิทธิ์ย่อยรหัส {permission.ActionId}");
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return Ok(await LoadActionPermissions(employeeId.Trim(), cancellationToken));
    }

    private async Task<IReadOnlyList<PagePermissionDto>> LoadPermissions(
        string employeeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT p.id, p.page_key, p.page_name, p.route_path,
                   p.category_name, p.display_order,
                   COALESCE(ep.can_access, FALSE)
            FROM public.application_pages p
            LEFT JOIN public.employee_page_permissions ep
              ON ep.application_page_id = p.id
             AND ep.employee_id = @employee_id
            WHERE p.is_active = TRUE
            ORDER BY p.display_order, p.id
            """;

        var result = new List<PagePermissionDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", employeeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PagePermissionDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetBoolean(6)));
        }

        return result;
    }

    private async Task<IReadOnlyList<PageActionPermissionDto>> LoadActionPermissions(
        string employeeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT action.id, page.id, page.page_key, page.page_name,
                   page.category_name, action.action_key, action.action_name,
                   action.description, action.display_order,
                   COALESCE(permission.can_execute, FALSE)
            FROM public.application_pages page
            JOIN public.application_page_actions action
              ON action.application_page_id = page.id AND action.is_active = TRUE
            LEFT JOIN public.employee_page_action_permissions permission
              ON permission.application_page_action_id = action.id
             AND permission.employee_id = @employee_id
            WHERE page.is_active = TRUE
            ORDER BY page.display_order, page.id, action.display_order, action.id
            """;
        var result = new List<PageActionPermissionDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", employeeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PageActionPermissionDto(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8), reader.GetBoolean(9)));
        }
        return result;
    }

    private async Task<IReadOnlyList<ApplicationPageAvailabilityDto>> LoadAvailability(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, page_key, page_name, route_path, category_name,
                   display_order, is_enabled, availability_updated_at,
                   availability_updated_by_name
            FROM public.application_pages
            WHERE is_active = TRUE
            ORDER BY display_order, id
            """;

        var result = new List<ApplicationPageAvailabilityDto>();
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ApplicationPageAvailabilityDto(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return result;
    }

    private async Task<string?> ResolveAuthenticatedEmployeeId(CancellationToken cancellationToken)
    {
        var tenantId = User.FindFirstValue("tid");
        var objectId = User.FindFirstValue("oid");
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId))
            return null;

        const string sql = """
            SELECT employee_id
            FROM public.microsoft_accounts
            WHERE tenant_id = @tenant_id
              AND entra_object_id = @object_id
              AND is_active = TRUE
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("object_id", objectId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}
