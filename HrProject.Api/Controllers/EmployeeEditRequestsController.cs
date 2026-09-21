using System.Text.Json;
using System.Security.Claims;
using HrProject.Api.Services;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/employee-edit-requests")]
public sealed class EmployeeEditRequestsController(
    NpgsqlDataSource dataSource,
    PageAccessService pageAccessService,
    PageActionPermissionService actionPermissionService,
    IConfiguration configuration,
    LotusNotesOutboxSignal lotusNotesOutboxSignal,
    WorkflowEmailNotificationService workflowNotificationService,
    ILogger<EmployeeEditRequestsController> logger) : ControllerBase
{
    private static readonly HashSet<string> AllowedFields =
    [
        "profileImage", "title", "firstName", "lastName", "thaiFullName", "englishFullName", "nickname",
        "lotusNotesEmail", "email", "personalMobile", "homePhone",
        "personal.nationalId", "personal.birthDate", "personal.gender",
        "personal.religion", "personal.bloodType", "personal.residenceProvince",
        "personal.residenceDistrict", "personal.residenceSubdistrict", "personal.residencePostalCode",
        "personal.currentAddress", "personal.idCardAddress", "personal.houseRegistrationAddress",
        "personal.emergencyContactName", "personal.emergencyContactPhone", "personal.emergencyContactAddress",
        "internal.company", "internal.businessUnit", "internal.division", "internal.department", "internal.section",
        "internal.position", "internal.jobCode", "internal.supervisor", "internal.supervisorEmployeeId",
        "internal.leaveApprover", "internal.leaveApproverEmployeeId",
        "internal.functionalSupervisor", "internal.buddy", "internal.employmentType", "internal.workSchedule",
        "internal.workLocation", "internal.extension", "internal.directPhone", "internal.companyMobile",
        "internal.macAddress", "internal.branchCode", "internal.branchName", "internal.responsibilityProvince",
        "internal.checklistType", "internal.productsResponsible", "internal.startDate", "internal.appointmentDate",
        "internal.providentFundStartDate", "internal.workExperienceType", "internal.hasCompanyParking",
        "internal.employeeStatus", "internal.excludeAttendance",
        "work.history",
        "education.history",
        "education.level", "education.institution", "education.major",
        "education.graduationYear",
        "training.history",
        "family.maritalStatus", "family.spouseName", "family.spouseNationalId", "family.phone",
        "family.currentAddressMapUrl"
    ];

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EmployeeEditRequestDto>>> GetAll(
        [FromQuery] string? employeeId,
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        var requestsOwnData = !string.IsNullOrWhiteSpace(employeeId) &&
                              string.Equals(employeeId, actor, StringComparison.OrdinalIgnoreCase);
        if (!requestsOwnData &&
            (!await pageAccessService.HasAccess(actor, "EMPLOYEE_EDIT_REQUESTS", cancellationToken) ||
             !await actionPermissionService.HasPermission(actor, "EMPLOYEE_EDIT_REQUESTS", "VIEW_ALL", cancellationToken)))
            return Forbid();

        const string sql = """
            SELECT id, request_no, employee_id, employee_name,
                   changes_json::text, request_reason, status,
                   requested_by_name, requested_at
            FROM public.employee_edit_requests
            WHERE (@employee_id IS NULL OR employee_id = @employee_id)
              AND (@status IS NULL OR status = @status)
            ORDER BY requested_at DESC, id DESC
            """;

        var result = new List<EmployeeEditRequestDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<string?>("employee_id", employeeId));
        command.Parameters.Add(new NpgsqlParameter<string?>("status", status));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadRequest(reader));

        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<EmployeeEditRequestDto>> Create(
        CreateEmployeeEditRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        if (!string.Equals(actor, request.RequestedBy, StringComparison.OrdinalIgnoreCase))
            return Forbid();

        var changes = request.Changes?
            .Where(change =>
                AllowedFields.Contains(change.FieldKey) &&
                !string.Equals(change.OldValue, change.NewValue, StringComparison.Ordinal))
            .GroupBy(change => change.FieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList() ?? [];

        if (string.IsNullOrWhiteSpace(request.EmployeeId) ||
            string.IsNullOrWhiteSpace(request.EmployeeName) ||
            changes.Count == 0 ||
            changes.Count > AllowedFields.Count ||
            changes.Any(change => string.IsNullOrWhiteSpace(change.FieldName)) ||
            string.IsNullOrWhiteSpace(request.RequestReason) ||
            string.IsNullOrWhiteSpace(request.RequestedBy) ||
            string.IsNullOrWhiteSpace(request.RequestedByName))
        {
            return BadRequest("กรุณาระบุข้อมูลที่ต้องการแก้ไขและเหตุผลให้ครบถ้วน");
        }

        var isOwnRequest = string.Equals(
            actor, request.EmployeeId, StringComparison.OrdinalIgnoreCase);
        if (!isOwnRequest)
        {
            var canRequestForOthers = await actionPermissionService.HasPermission(
                actor, "EMPLOYEES", "REQUEST_EDIT", cancellationToken);
            var requiresCompanyPermission = changes.Any(change =>
                change.FieldKey.StartsWith("internal.", StringComparison.OrdinalIgnoreCase) ||
                change.FieldKey is "lotusNotesEmail" or "email" or "work.history");
            var requiresPersonalPermission = changes.Any(change =>
                !change.FieldKey.StartsWith("internal.", StringComparison.OrdinalIgnoreCase) &&
                change.FieldKey is not ("lotusNotesEmail" or "email" or "work.history"));

            var canRequestCompanyChange = !requiresCompanyPermission ||
                await actionPermissionService.HasPermission(
                    actor, "EMPLOYEES", "VIEW_COMPANY", cancellationToken);
            var canRequestPersonalChange = !requiresPersonalPermission ||
                await actionPermissionService.HasPermission(
                    actor, "EMPLOYEES", "VIEW_PERSONAL", cancellationToken);

            if (!canRequestForOthers || !canRequestCompanyChange || !canRequestPersonalChange)
                return Forbid();
        }

        var profileImageChange = changes.FirstOrDefault(change =>
            string.Equals(change.FieldKey, "profileImage", StringComparison.OrdinalIgnoreCase));
        if (profileImageChange is not null && !IsValidProfileImage(profileImageChange.NewValue))
            return BadRequest("รูปโปรไฟล์ไม่ถูกต้องหรือมีขนาดใหญ่เกิน 2 MB");

        var employeeStatusChange = changes.FirstOrDefault(change =>
            string.Equals(change.FieldKey, "internal.employeeStatus", StringComparison.OrdinalIgnoreCase));
        if (employeeStatusChange is not null && !EmployeeStatusValues.IsValid(employeeStatusChange.NewValue))
            return BadRequest("สถานะพนักงานต้องเป็น พนักงาน หรือ ลาออก เท่านั้น");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string insertSql = """
            INSERT INTO public.employee_edit_requests
                (request_no, employee_id, employee_name, changes_json,
                 request_reason, requested_by, requested_by_name)
            VALUES
                (@temporary_no, @employee_id, @employee_name, @changes_json,
                 @reason, @requested_by, @requested_by_name)
            RETURNING id
            """;

        long id;
        try
        {
            await using var command = new NpgsqlCommand(insertSql, connection, transaction);
            command.Parameters.AddWithValue("temporary_no", $"TMP-{Guid.NewGuid():N}"[..30]);
            command.Parameters.AddWithValue("employee_id", request.EmployeeId.Trim());
            command.Parameters.AddWithValue("employee_name", request.EmployeeName.Trim());
            command.Parameters.Add(
                new NpgsqlParameter("changes_json", NpgsqlDbType.Jsonb)
                {
                    Value = JsonSerializer.Serialize(changes)
                });
            command.Parameters.AddWithValue("reason", request.RequestReason.Trim());
            command.Parameters.AddWithValue("requested_by", request.RequestedBy.Trim());
            command.Parameters.AddWithValue("requested_by_name", request.RequestedByName.Trim());
            id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Conflict("พนักงานคนนี้มีคำขอแก้ไขข้อมูลที่รออนุมัติอยู่แล้ว");
        }

        var requestNo = $"EER-{DateTime.Today.Year}-{id:000000}";
        await using (var command = new NpgsqlCommand(
            "UPDATE public.employee_edit_requests SET request_no = @request_no WHERE id = @id",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("request_no", requestNo);
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var details = string.Join("; ", changes.Select(DescribeChange));
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.employee_edit_request_history
                (employee_edit_request_id, action, details_text,
                 action_by, action_by_name)
            VALUES
                (@request_id, 'CREATE_REQUEST', @details,
                 @action_by, @action_by_name)
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("request_id", id);
            command.Parameters.AddWithValue("details", details);
            command.Parameters.AddWithValue("action_by", request.RequestedBy.Trim());
            command.Parameters.AddWithValue("action_by_name", request.RequestedByName.Trim());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        try
        {
            await workflowNotificationService.SendAsync(
                "EMPLOYEE_EDIT_REQUESTS", actor,
                "มีคำขอแก้ไขข้อมูลพนักงานรอตรวจสอบ",
                $"เลขที่คำขอ {requestNo}\nพนักงาน: {request.EmployeeName.Trim()} ({request.EmployeeId.Trim()})\nเหตุผล: {request.RequestReason.Trim()}",
                "/employees/edit-requests", CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Employee edit request {RequestId} was saved but email notification failed", id);
        }
        var created = await FindById(id, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    private static string DescribeChange(EmployeeFieldChangeDto change) =>
        string.Equals(change.FieldKey, "profileImage", StringComparison.OrdinalIgnoreCase)
            ? "รูปโปรไฟล์: เปลี่ยนรูปโปรไฟล์"
            : $"{change.FieldName}: {change.OldValue} → {change.NewValue}";

    private static bool IsValidProfileImage(string value) =>
        value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) &&
        value.Length <= 3_000_000;

    [HttpPut("{id:long}/approve")]
    public Task<ActionResult<EmployeeEditRequestDto>> Approve(
        long id,
        ReviewEmployeeEditRequest request,
        CancellationToken cancellationToken) =>
        Review(id, "APPROVED", "APPROVE", request, cancellationToken);

    [HttpPut("{id:long}/reject")]
    public Task<ActionResult<EmployeeEditRequestDto>> Reject(
        long id,
        ReviewEmployeeEditRequest request,
        CancellationToken cancellationToken) =>
        Review(id, "REJECTED", "REJECT", request, cancellationToken);

    private async Task<ActionResult<EmployeeEditRequestDto>> Review(
        long id,
        string newStatus,
        string action,
        ReviewEmployeeEditRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        if (!string.Equals(actor, request.ReviewedBy, StringComparison.OrdinalIgnoreCase) ||
            !await pageAccessService.HasAccess(actor, "EMPLOYEE_EDIT_REQUESTS", cancellationToken) ||
            !await actionPermissionService.HasPermission(actor, "EMPLOYEE_EDIT_REQUESTS", action, cancellationToken))
            return Forbid();

        if (id <= 0 ||
            string.IsNullOrWhiteSpace(request.ReviewedBy) ||
            string.IsNullOrWhiteSpace(request.ReviewedByName))
        {
            return BadRequest("ข้อมูลผู้ดำเนินการไม่ครบถ้วน");
        }

        var pendingRequest = await FindById(id, cancellationToken);
        if (pendingRequest is null)
            return NotFound("ไม่พบเอกสารขอแก้ไขข้อมูลพนักงาน");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string updateSql = """
            UPDATE public.employee_edit_requests
            SET status = @status,
                reviewed_by = @reviewed_by,
                reviewed_by_name = @reviewed_by_name,
                reviewed_at = CURRENT_TIMESTAMP,
                review_remark = @review_remark
            WHERE id = @id
              AND status = 'PENDING'
            """;

        await using (var command = new NpgsqlCommand(updateSql, connection, transaction))
        {
            command.Parameters.AddWithValue("status", newStatus);
            command.Parameters.AddWithValue("reviewed_by", request.ReviewedBy.Trim());
            command.Parameters.AddWithValue("reviewed_by_name", request.ReviewedByName.Trim());
            command.Parameters.Add(
                new NpgsqlParameter<string?>(
                    "review_remark",
                    string.IsNullOrWhiteSpace(request.ReviewRemark)
                        ? null
                        : request.ReviewRemark.Trim()));
            command.Parameters.AddWithValue("id", id);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Conflict("เอกสารไม่ได้อยู่ในสถานะรออนุมัติหรือไม่พบเอกสาร");
            }
        }

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.employee_edit_request_history
                (employee_edit_request_id, action, details_text,
                 action_by, action_by_name)
            VALUES
                (@request_id, @action, @details,
                 @action_by, @action_by_name)
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("request_id", id);
            command.Parameters.AddWithValue("action", action);
            command.Parameters.AddWithValue(
                "details",
                string.IsNullOrWhiteSpace(request.ReviewRemark)
                    ? (newStatus == "APPROVED" ? "อนุมัติคำขอแก้ไขข้อมูลพนักงาน" : "ไม่อนุมัติคำขอแก้ไขข้อมูลพนักงาน")
                    : $"{(newStatus == "APPROVED" ? "อนุมัติ" : "ไม่อนุมัติ")}; หมายเหตุ: {request.ReviewRemark.Trim()}");
            command.Parameters.AddWithValue("action_by", request.ReviewedBy.Trim());
            command.Parameters.AddWithValue("action_by_name", request.ReviewedByName.Trim());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var queuedForLotusNotes = false;
        if (newStatus == "APPROVED")
        {
            var employeeId = await EmployeesController.ApplyApprovedChanges(
                connection, transaction, pendingRequest.EmployeeId,
                pendingRequest.Changes, cancellationToken);
            if (!employeeId.HasValue)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Conflict("ไม่พบข้อมูลพนักงานสำหรับปรับปรุง จึงยังไม่ได้อนุมัติคำขอ");
            }

            var identity = await GetLotusNotesIdentity(
                connection, transaction, employeeId.Value, cancellationToken);
            var changesThaiNameParts = pendingRequest.Changes.Any(change =>
                change.FieldKey is "firstName" or "lastName");
            var explicitlyChangesThaiFullName = pendingRequest.Changes.Any(change =>
                change.FieldKey == "thaiFullName");
            var resolvedThaiFullName = changesThaiNameParts && !explicitlyChangesThaiFullName
                ? $"{identity.FirstName} {identity.LastName}".Trim()
                : identity.EmployeeName;
            var payload = LotusNotesEmployeePayloadBuilder.BuildApprovedChanges(
                pendingRequest.Changes, resolvedThaiFullName);

            if (payload is not null)
            {
                if (identity.NationalId.Length != 13 ||
                    identity.NationalId.Any(character => !char.IsAsciiDigit(character)))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Conflict("ไม่สามารถอนุมัติได้ เนื่องจากพนักงานไม่มีเลขบัตรประชาชน 13 หลักสำหรับใช้เป็น Key ของ Lotus Notes");
                }

                const string outboxSql = """
                    INSERT INTO public.lotus_notes_employee_outbox
                        (employee_edit_request_id, employee_id, employee_code, employee_name,
                         database_name, external_key, payload, status)
                    VALUES
                        (@request_id, @employee_id, @employee_code, @employee_name,
                         @database_name, @external_key, @payload::jsonb, 'PENDING')
                    """;
                await using var outboxCommand = new NpgsqlCommand(outboxSql, connection, transaction);
                outboxCommand.Parameters.AddWithValue("request_id", id);
                outboxCommand.Parameters.AddWithValue("employee_id", employeeId.Value);
                outboxCommand.Parameters.AddWithValue("employee_code", pendingRequest.EmployeeId);
                outboxCommand.Parameters.AddWithValue("employee_name", resolvedThaiFullName);
                outboxCommand.Parameters.AddWithValue("database_name",
                    Environment.GetEnvironmentVariable("LOTUS_NOTES_DATABASE") ??
                    configuration["LotusNotes:Database"] ?? "Employee");
                outboxCommand.Parameters.AddWithValue("external_key", identity.NationalId);
                outboxCommand.Parameters.AddWithValue("payload", payload);
                await outboxCommand.ExecuteNonQueryAsync(cancellationToken);
                queuedForLotusNotes = true;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        if (queuedForLotusNotes)
            lotusNotesOutboxSignal.Notify();

        return Ok(await FindById(id, cancellationToken));

        /* Previous non-atomic update flow retained temporarily for migration context.
        if (newStatus == "APPROVED" &&
            !await EmployeesController.ApplyApprovedChanges(
                dataSource,
                pendingRequest.EmployeeId,
                pendingRequest.Changes,
                cancellationToken))
        {
            return Conflict("อนุมัติเอกสารแล้ว แต่ไม่พบข้อมูลพนักงานสำหรับปรับปรุง");
        }

        return Ok(await FindById(id, cancellationToken)); */
    }

    private static async Task<LotusNotesEmployeeIdentity> GetLotusNotesIdentity(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long employeeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COALESCE(BTRIM(personal.national_id), ''),
                   COALESCE(BTRIM(basic.first_name_th), ''),
                   COALESCE(BTRIM(basic.last_name_th), ''),
                   COALESCE(NULLIF(BTRIM(basic.full_name_th), ''),
                            BTRIM(CONCAT_WS(' ', basic.first_name_th, basic.last_name_th)), '')
            FROM public.employees employee
            LEFT JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
            LEFT JOIN public.employee_personal_info personal ON personal.employee_id = employee.id
            WHERE employee.id = @employee_id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("employee_id", employeeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Employee disappeared while applying an approved edit request.");
        return new LotusNotesEmployeeIdentity(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    private sealed record LotusNotesEmployeeIdentity(
        string NationalId,
        string FirstName,
        string LastName,
        string EmployeeName);

    private async Task<EmployeeEditRequestDto?> FindById(
        long id,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, request_no, employee_id, employee_name,
                   changes_json::text, request_reason, status,
                   requested_by_name, requested_at
            FROM public.employee_edit_requests
            WHERE id = @id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRequest(reader) : null;
    }

    private static EmployeeEditRequestDto ReadRequest(NpgsqlDataReader reader)
    {
        var changes = JsonSerializer.Deserialize<List<EmployeeFieldChangeDto>>(
            reader.GetString(4),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        return new EmployeeEditRequestDto(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            changes,
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetFieldValue<DateTimeOffset>(8));
    }

    private async Task<string?> ResolveAuthenticatedEmployeeId(CancellationToken cancellationToken)
    {
        var employeeId = User.FindFirstValue("employee_id");
        if (!string.IsNullOrWhiteSpace(employeeId)) return employeeId;

        var tenantId = User.FindFirstValue("tid");
        var objectId = User.FindFirstValue("oid");
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId)) return null;

        const string sql = """
            SELECT employee_id
            FROM public.microsoft_accounts
            WHERE tenant_id = @tenant_id AND entra_object_id = @object_id AND is_active = TRUE
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("object_id", objectId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}
