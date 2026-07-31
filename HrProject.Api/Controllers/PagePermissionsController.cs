using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/page-permissions")]
public sealed class PagePermissionsController(NpgsqlDataSource dataSource) : ControllerBase
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
}
