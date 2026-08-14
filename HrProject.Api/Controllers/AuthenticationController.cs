using System.Security.Claims;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/auth")]
[Authorize(Policy = "HrApiScope")]
public sealed class AuthenticationController(NpgsqlDataSource dataSource) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<CurrentMicrosoftUserDto>> GetCurrentUser(
        CancellationToken cancellationToken)
    {
        var tenantId = Claim("tid");
        var objectId = Claim("oid");
        var email = Claim("preferred_username")
            ?? Claim("upn")
            ?? Claim("email")
            ?? User.FindFirstValue(ClaimTypes.Email);
        var displayName = Claim("name")
            ?? User.Identity?.Name
            ?? email;

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(objectId) ||
            string.IsNullOrWhiteSpace(email))
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                "บัญชี Microsoft ไม่มี tenant, object id หรือ email ที่จำเป็น");
        }

        var normalizedEmail = email.Trim().ToUpperInvariant();
        var employee = await EmployeesController.FindByFullName(
            dataSource,
            displayName ?? string.Empty,
            cancellationToken);

        const string sql = """
            INSERT INTO public.microsoft_accounts
                (tenant_id, entra_object_id, employee_email,
                 employee_email_normalized, employee_id, display_name,
                 user_principal_name)
            VALUES
                (@tenant_id, @object_id, @email,
                 @normalized_email, @employee_id, @display_name,
                 @principal_name)
            ON CONFLICT (tenant_id, entra_object_id)
            DO UPDATE SET
                employee_email = EXCLUDED.employee_email,
                employee_email_normalized = EXCLUDED.employee_email_normalized,
                employee_id = EXCLUDED.employee_id,
                display_name = EXCLUDED.display_name,
                user_principal_name = EXCLUDED.user_principal_name,
                is_active = TRUE,
                last_sign_in_at = CURRENT_TIMESTAMP
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("object_id", objectId);
        command.Parameters.AddWithValue("email", email.Trim());
        command.Parameters.AddWithValue("normalized_email", normalizedEmail);
        command.Parameters.Add(new NpgsqlParameter<string?>("employee_id", employee?.EmployeeCode));
        command.Parameters.AddWithValue("display_name", displayName ?? employee?.FullName ?? email.Trim());
        command.Parameters.AddWithValue("principal_name", email.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);

        return Ok(new CurrentMicrosoftUserDto(
            tenantId,
            objectId,
            email.Trim(),
            displayName ?? employee?.FullName ?? email.Trim(),
            employee?.EmployeeCode ?? string.Empty,
            employee?.FullName ?? displayName ?? email.Trim(),
            employee?.Department ?? string.Empty,
            employee?.Position ?? string.Empty,
            employee is not null));
    }

    private string? Claim(string type) => User.FindFirstValue(type);
}
