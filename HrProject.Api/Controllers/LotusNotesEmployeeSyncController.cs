using System.Text.Json;
using HrProject.Api.Services;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/lotus-notes-employee-sync")]
[Authorize(Policy = "HrApiScope")]
public sealed class LotusNotesEmployeeSyncController(
    NpgsqlDataSource dataSource,
    PageAccessService pageAccessService,
    PageActionPermissionService actionPermissionService,
    LotusNotesOutboxSignal lotusNotesOutboxSignal) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LotusNotesSyncItemDto>>> GetAll(CancellationToken token)
    {
        var actor = await ResolveActor(token);
        if (actor is null) return Unauthorized();
        if (!await pageAccessService.HasAccess(actor.Value.EmployeeId, "LOTUS_NOTES_EMPLOYEE_SYNC", token)) return Forbid();

        const string sql = """
            SELECT id, pre_employee_id, employee_edit_request_id, employee_id, employee_code, employee_name,
                   database_name, external_key, payload::text, status, attempt_count,
                   created_at, updated_at, last_attempt_at, succeeded_at, last_error,
                   response_summary, retry_requested_at, retry_requested_name
            FROM public.lotus_notes_employee_outbox
            ORDER BY CASE status WHEN 'FAILED' THEN 0 WHEN 'PENDING' THEN 1 WHEN 'PROCESSING' THEN 2 ELSE 3 END,
                     created_at DESC, id DESC
            """;
        var result = new List<LotusNotesSyncItemDto>();
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            using var payload = JsonDocument.Parse(reader.GetString(8));
            result.Add(new LotusNotesSyncItemDto(
                reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7), payload.RootElement.Clone(),
                reader.GetString(9), reader.GetInt32(10), reader.GetFieldValue<DateTimeOffset>(11),
                reader.GetFieldValue<DateTimeOffset>(12), Time(reader, 13), Time(reader, 14),
                Text(reader, 15), Text(reader, 16), Time(reader, 17), Text(reader, 18)));
        }
        return Ok(result);
    }

    [HttpPost("{id:long}/retry")]
    public async Task<IActionResult> Retry(long id, CancellationToken token)
    {
        var actor = await ResolveActor(token);
        if (actor is null) return Unauthorized();
        if (!await pageAccessService.HasAccess(actor.Value.EmployeeId, "LOTUS_NOTES_EMPLOYEE_SYNC", token) ||
            !await actionPermissionService.HasPermission(actor.Value.EmployeeId, "LOTUS_NOTES_EMPLOYEE_SYNC", "RETRY", token))
            return Forbid();

        const string sql = """
            UPDATE public.lotus_notes_employee_outbox
            SET status='PENDING', last_error=NULL, response_summary=NULL, succeeded_at=NULL,
                retry_requested_at=CURRENT_TIMESTAMP, retry_requested_by=@actor,
                retry_requested_name=@actor_name, updated_at=CURRENT_TIMESTAMP
            WHERE id=@id AND status='FAILED'
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("actor", actor.Value.EmployeeId);
        command.Parameters.AddWithValue("actor_name", actor.Value.Name);
        if (await command.ExecuteNonQueryAsync(token) == 0)
            return Conflict("สามารถ Retry ได้เฉพาะรายการที่ส่งไม่สำเร็จเท่านั้น");
        lotusNotesOutboxSignal.Notify();
        return NoContent();
    }

    private async Task<(string EmployeeId, string Name)?> ResolveActor(CancellationToken token)
    {
        var employeeId = User.FindFirst("employee_id")?.Value;
        if (!string.IsNullOrWhiteSpace(employeeId))
            return (employeeId.Trim(), User.FindFirst("name")?.Value ?? employeeId.Trim());
        var tenant = User.FindFirst("tid")?.Value;
        var objectId = User.FindFirst("oid")?.Value;
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(objectId)) return null;
        await using var command = dataSource.CreateCommand("SELECT employee_id,COALESCE(NULLIF(display_name,''),employee_id) FROM public.microsoft_accounts WHERE tenant_id=@tenant AND entra_object_id=@object AND is_active=TRUE AND employee_id IS NOT NULL LIMIT 1");
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("object", objectId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset? Time(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}
