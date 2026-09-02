using System.Security.Claims;
using HrProject.Api.Services;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/manager-leave-notifications")]
[Authorize(Policy = "HrApiScope")]
public sealed class ManagerLeaveNotificationsController(
    NpgsqlDataSource dataSource,
    MicrosoftGraphMailService mailService,
    PageActionPermissionService actionPermissionService) : ControllerBase
{
    [HttpGet("recipients")]
    public async Task<ActionResult<IReadOnlyList<ManagerNotificationRecipientDto>>> GetRecipients(
        CancellationToken cancellationToken)
    {
        var actor = await GetActor(cancellationToken);
        if (actor is null)
            return Forbid();
        var canSendForAnyEmployee = await actionPermissionService.HasPermission(
            actor.EmployeeCode, "LEAVE_TEAM", "CREATE_FOR_OTHERS", cancellationToken);
        const string sql = """
            WITH manager AS
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
                WHERE e.id = @manager_id
            )
            SELECT e.employee_code,
                   COALESCE(NULLIF(b.full_name_th, ''), NULLIF(b.full_name_en, ''), e.employee_code),
                   COALESCE(NULLIF(msa.employee_email, ''), NULLIF(b.email_address, ''), NULLIF(b.email_alias, ''), ''),
                   COALESCE(c.department, ''), COALESCE(c.position_name, ''),
                   REGEXP_REPLACE(UPPER(BTRIM(COALESCE(c.supervisor_name, ''))), '\s+', ' ', 'g') = ANY(m.names),
                   REGEXP_REPLACE(UPPER(BTRIM(COALESCE(c.leave_approver_name, ''))), '\s+', ' ', 'g') = ANY(m.names)
            FROM public.employees e
            JOIN public.employee_basic_info b ON b.employee_id = e.id
            JOIN public.employee_company_info c ON c.employee_id = e.id
            LEFT JOIN LATERAL
            (
                SELECT ma.employee_email
                FROM public.microsoft_accounts ma
                WHERE ma.employee_id = e.employee_code AND ma.is_active = TRUE
                  AND POSITION('@' IN ma.employee_email) > 1
                ORDER BY ma.last_sign_in_at DESC NULLS LAST
                LIMIT 1
            ) msa ON TRUE
            CROSS JOIN manager m
            WHERE e.is_active = TRUE
              AND e.id <> m.id
              AND (@can_send_all OR
                  REGEXP_REPLACE(UPPER(BTRIM(COALESCE(c.supervisor_name, ''))), '\s+', ' ', 'g') = ANY(m.names)
                  OR REGEXP_REPLACE(UPPER(BTRIM(COALESCE(c.leave_approver_name, ''))), '\s+', ' ', 'g') = ANY(m.names)
              )
            ORDER BY COALESCE(NULLIF(b.full_name_th, ''), NULLIF(b.full_name_en, ''), e.employee_code)
            """;

        var result = new List<ManagerNotificationRecipientDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("manager_id", actor.EmployeeDatabaseId);
        command.Parameters.AddWithValue("can_send_all", canSendForAnyEmployee);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ManagerNotificationRecipientDto(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                reader.GetBoolean(5), reader.GetBoolean(6)));
        }

        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ManagerLeaveNotificationDto>>> GetHistory(
        CancellationToken cancellationToken)
    {
        var actor = await GetActor(cancellationToken);
        if (actor is null)
            return Forbid();
        const string sql = """
            SELECT n.id, n.notification_no, se.employee_code, n.sender_name, n.sender_email,
                   re.employee_code, n.recipient_name, n.recipient_email,
                   n.leave_type_id, leave_type.name_th,
                   n.start_date, n.start_time, n.end_time, n.leave_hours, n.details,
                   n.email_status, n.created_at, n.sent_at, n.email_error
            FROM public.manager_leave_notifications n
            JOIN public.employees se ON se.id = n.sender_employee_id
            JOIN public.employees re ON re.id = n.recipient_employee_id
            LEFT JOIN public.leave_types leave_type ON leave_type.id = n.leave_type_id
            WHERE n.sender_employee_id = @sender_id
            ORDER BY n.created_at DESC, n.id DESC
            """;

        var result = new List<ManagerLeaveNotificationDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("sender_id", actor.EmployeeDatabaseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadNotification(reader));
        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<ManagerLeaveNotificationDto>> Send(
        CreateManagerLeaveNotificationRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await GetActor(cancellationToken);
        if (actor is null)
            return Forbid();
        var canSendForAnyEmployee = await actionPermissionService.HasPermission(
            actor.EmployeeCode, "LEAVE_TEAM", "CREATE_FOR_OTHERS", cancellationToken);
        if (string.IsNullOrWhiteSpace(request.RecipientEmployeeId))
            return BadRequest("กรุณาเลือกลูกน้อง");
        if (request.LeaveTypeId <= 0)
            return BadRequest("กรุณาเลือกประเภทการลา");
        var leaveDate = DateOnly.FromDateTime(DateTime.Today);

        var recipient = await FindAllowedRecipient(
            actor.EmployeeDatabaseId, request.RecipientEmployeeId,
            canSendForAnyEmployee, cancellationToken);
        if (recipient is null)
            return BadRequest("พนักงานที่เลือกไม่ได้มี Boss หรือ Reporting To (Leave Approve) เป็นคุณ");
        if (!recipient.Email.Contains('@', StringComparison.Ordinal))
            return BadRequest("พนักงานที่เลือกยังไม่มีอีเมลที่สามารถส่งได้");

        var leaveTypeName = await FindLeaveTypeName(request.LeaveTypeId, cancellationToken);
        if (string.IsNullOrWhiteSpace(leaveTypeName))
            return BadRequest("ไม่พบประเภทการลาที่เลือก หรือประเภทการลานี้ถูกปิดใช้งานแล้ว");

        const string insertSql = """
            WITH next_id AS
            (
                SELECT nextval(pg_get_serial_sequence('public.manager_leave_notifications', 'id')) AS id
            )
            INSERT INTO public.manager_leave_notifications
                (id, notification_no, sender_employee_id, recipient_employee_id,
                 sender_name, sender_email, recipient_name, recipient_email,
                 subject, leave_type_id, start_date, end_date, start_time, end_time, leave_hours, details, email_status)
            SELECT id, 'INF-' || TO_CHAR(CURRENT_DATE, 'YYYYMMDD') || '-' || LPAD(id::text, 6, '0'),
                   @sender_id, @recipient_id, @sender_name, @sender_email,
                   @recipient_name, @recipient_email, @subject, @leave_type_id, @leave_date, @leave_date,
                   NULL, NULL, NULL, @details, 'PENDING'
            FROM next_id
            RETURNING id
            """;

        long notificationId;
        await using (var command = dataSource.CreateCommand(insertSql))
        {
            command.Parameters.AddWithValue("sender_id", actor.EmployeeDatabaseId);
            command.Parameters.AddWithValue("recipient_id", recipient.DatabaseId);
            command.Parameters.AddWithValue("sender_name", actor.Name);
            command.Parameters.AddWithValue("sender_email", actor.Email);
            command.Parameters.AddWithValue("recipient_name", recipient.Name);
            command.Parameters.AddWithValue("recipient_email", recipient.Email);
            command.Parameters.AddWithValue("subject", $"แจ้งการลา ({leaveTypeName})");
            command.Parameters.AddWithValue("leave_type_id", request.LeaveTypeId);
            command.Parameters.AddWithValue("leave_date", leaveDate);
            command.Parameters.AddWithValue("details", request.Note?.Trim() ?? string.Empty);
            notificationId = (long)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("สร้างประวัติการแจ้งไม่สำเร็จ"));
        }

        try
        {
            var emailSubject = $"[HR Info] แจ้งการลา ({leaveTypeName}) {recipient.Name} วันที่ {leaveDate:dd/MM/yyyy}";
            var body = MicrosoftGraphMailService.BuildBody(
                actor.Name, recipient.Name, leaveTypeName, leaveDate,
                request.Note?.Trim() ?? string.Empty);
            await mailService.SendAsync(
                actor.Email, recipient.Email, emailSubject, body, cancellationToken);
            await UpdateSendStatus(notificationId, "SENT", null, cancellationToken);
        }
        catch (Exception exception)
        {
            await UpdateSendStatus(notificationId, "FAILED", exception.Message, cancellationToken);
            return StatusCode(StatusCodes.Status502BadGateway,
                $"บันทึกข้อมูลแล้ว แต่ส่งอีเมลไม่สำเร็จ: {exception.Message}");
        }

        var created = await FindNotification(notificationId, actor.EmployeeDatabaseId, cancellationToken);
        return CreatedAtAction(nameof(GetHistory), created);
    }

    private async Task<Actor?> GetActor(CancellationToken cancellationToken)
    {
        var tenantId = User.FindFirstValue("tid");
        var objectId = User.FindFirstValue("oid");
        var email = User.FindFirstValue("preferred_username")
            ?? User.FindFirstValue("upn")
            ?? User.FindFirstValue("email");
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId))
            return null;

        const string sql = """
            SELECT e.id, e.employee_code,
                   COALESCE(NULLIF(b.full_name_th, ''), NULLIF(b.full_name_en, ''), ma.display_name),
                   COALESCE(NULLIF(@claim_email, ''), NULLIF(ma.employee_email, ''), NULLIF(b.email_address, ''), '')
            FROM public.microsoft_accounts ma
            JOIN public.employees e ON e.employee_code = ma.employee_id
            JOIN public.employee_basic_info b ON b.employee_id = e.id
            WHERE ma.tenant_id = @tenant_id AND ma.entra_object_id = @object_id
              AND ma.is_active = TRUE AND e.is_active = TRUE
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("object_id", objectId);
        command.Parameters.AddWithValue("claim_email", email ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new Actor(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    private async Task<Recipient?> FindAllowedRecipient(
        long managerId, string employeeCode, bool canSendForAnyEmployee,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH manager AS
            (
                SELECT ARRAY_REMOVE(ARRAY[
                    NULLIF(REGEXP_REPLACE(UPPER(BTRIM(COALESCE(b.full_name_th, ''))), '\s+', ' ', 'g'), ''),
                    NULLIF(REGEXP_REPLACE(UPPER(BTRIM(COALESCE(b.full_name_en, ''))), '\s+', ' ', 'g'), ''),
                    NULLIF(REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', b.first_name_th, b.last_name_th))), '\s+', ' ', 'g'), ''),
                    NULLIF(REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', b.first_name_en, b.last_name_en))), '\s+', ' ', 'g'), '')
                ], NULL) AS names
                FROM public.employee_basic_info b WHERE b.employee_id = @manager_id
            )
            SELECT e.id,
                   COALESCE(NULLIF(b.full_name_th, ''), NULLIF(b.full_name_en, ''), e.employee_code),
                   COALESCE(NULLIF(msa.employee_email, ''), NULLIF(b.email_address, ''), NULLIF(b.email_alias, ''), '')
            FROM public.employees e
            JOIN public.employee_basic_info b ON b.employee_id = e.id
            JOIN public.employee_company_info c ON c.employee_id = e.id
            LEFT JOIN LATERAL
            (
                SELECT ma.employee_email
                FROM public.microsoft_accounts ma
                WHERE ma.employee_id = e.employee_code AND ma.is_active = TRUE
                  AND POSITION('@' IN ma.employee_email) > 1
                ORDER BY ma.last_sign_in_at DESC NULLS LAST
                LIMIT 1
            ) msa ON TRUE
            CROSS JOIN manager m
            WHERE e.is_active = TRUE AND e.employee_code = @employee_code
              AND (@can_send_all OR
                  REGEXP_REPLACE(UPPER(BTRIM(COALESCE(c.supervisor_name, ''))), '\s+', ' ', 'g') = ANY(m.names)
                  OR REGEXP_REPLACE(UPPER(BTRIM(COALESCE(c.leave_approver_name, ''))), '\s+', ' ', 'g') = ANY(m.names)
              )
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("manager_id", managerId);
        command.Parameters.AddWithValue("employee_code", employeeCode.Trim());
        command.Parameters.AddWithValue("can_send_all", canSendForAnyEmployee);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new Recipient(reader.GetInt64(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private async Task UpdateSendStatus(
        long id, string status, string? error, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE public.manager_leave_notifications
            SET email_status = @status,
                email_error = @error,
                sent_at = CASE WHEN @status = 'SENT' THEN CURRENT_TIMESTAMP ELSE NULL END
            WHERE id = @id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.Add(new NpgsqlParameter<string?>("error", error));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string?> FindLeaveTypeName(
        long leaveTypeId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT name_th FROM public.leave_types WHERE id = @id AND is_active = TRUE";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", leaveTypeId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task<ManagerLeaveNotificationDto?> FindNotification(
        long id, long senderId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT n.id, n.notification_no, se.employee_code, n.sender_name, n.sender_email,
                   re.employee_code, n.recipient_name, n.recipient_email,
                   n.leave_type_id, leave_type.name_th,
                   n.start_date, n.start_time, n.end_time, n.leave_hours, n.details,
                   n.email_status, n.created_at, n.sent_at, n.email_error
            FROM public.manager_leave_notifications n
            JOIN public.employees se ON se.id = n.sender_employee_id
            JOIN public.employees re ON re.id = n.recipient_employee_id
            LEFT JOIN public.leave_types leave_type ON leave_type.id = n.leave_type_id
            WHERE n.id = @id AND n.sender_employee_id = @sender_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("sender_id", senderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNotification(reader) : null;
    }

    private static ManagerLeaveNotificationDto ReadNotification(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.GetFieldValue<DateOnly>(10),
        reader.IsDBNull(11) ? null : reader.GetFieldValue<TimeOnly>(11),
        reader.IsDBNull(12) ? null : reader.GetFieldValue<TimeOnly>(12),
        reader.IsDBNull(13) ? null : reader.GetDecimal(13), reader.GetString(14),
        reader.GetString(15), reader.GetFieldValue<DateTimeOffset>(16),
        reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
        reader.IsDBNull(18) ? null : reader.GetString(18));

    private sealed record Actor(long EmployeeDatabaseId, string EmployeeCode, string Name, string Email);
    private sealed record Recipient(long DatabaseId, string Name, string Email);
}
