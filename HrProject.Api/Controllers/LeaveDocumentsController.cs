using HrProject.Shared.Models;
using HrProject.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/leave-documents")]
public sealed class LeaveDocumentsController(
    NpgsqlDataSource dataSource,
    PageActionPermissionService actionPermissionService,
    PageAccessService pageAccessService,
    IWebHostEnvironment environment,
    LeaveApprovalEmailService approvalEmailService,
    LeaveDecisionEmailService decisionEmailService,
    LeaveCancellationEmailService cancellationEmailService,
    OutlookCalendarSyncService outlookCalendarSyncService,
    ILogger<LeaveDocumentsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LeaveDocumentDto>>> GetAll(
        [FromQuery] string? creatorEmployeeId,
        [FromQuery] string? approverEmployeeId,
        [FromQuery] string? actingEmployeeId,
        [FromQuery] string? status,
        [FromQuery] bool viewAll,
        CancellationToken cancellationToken)
    {
        var restrictPendingToApprover = false;
        if (viewAll)
        {
            var authenticatedEmployeeId = await ResolveAuthenticatedEmployeeId(cancellationToken);
            if (string.IsNullOrWhiteSpace(authenticatedEmployeeId) ||
                string.IsNullOrWhiteSpace(actingEmployeeId) ||
                !string.Equals(authenticatedEmployeeId, actingEmployeeId, StringComparison.OrdinalIgnoreCase))
                return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับพนักงานที่ขอดูข้อมูล");

            var canViewAll = await actionPermissionService.HasPermission(
                    actingEmployeeId, "LEAVE_ALL_DOCUMENTS", "VIEW_ALL", cancellationToken) ||
                await actionPermissionService.HasPermission(
                    actingEmployeeId, "LEAVE_DOCUMENTS", "VIEW_ALL", cancellationToken);
            if (!canViewAll)
                return StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์ดูเอกสารการลาของพนักงานทั้งหมด");

            creatorEmployeeId = null;
            approverEmployeeId = null;
        }
        else if (string.IsNullOrWhiteSpace(status))
        {
            var authenticatedEmployeeId = await ResolveAuthenticatedEmployeeId(cancellationToken);
            if (string.IsNullOrWhiteSpace(authenticatedEmployeeId))
                return StatusCode(StatusCodes.Status403Forbidden, "ไม่พบบัญชีพนักงานที่เชื่อมกับ Microsoft");
            if (!string.IsNullOrWhiteSpace(creatorEmployeeId) &&
                !string.Equals(authenticatedEmployeeId, creatorEmployeeId, StringComparison.OrdinalIgnoreCase))
                return StatusCode(StatusCodes.Status403Forbidden, "ไม่สามารถดูเอกสารการลาของพนักงานคนอื่นได้");

            creatorEmployeeId = authenticatedEmployeeId;
        }
        else if (string.Equals(status, "PENDING_APPROVAL", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(actingEmployeeId))
                return BadRequest("กรุณาระบุพนักงานผู้เปิดดูเอกสารรออนุมัติ");
            var authenticatedEmployeeId = await ResolveAuthenticatedEmployeeId(cancellationToken);
            if (string.IsNullOrWhiteSpace(authenticatedEmployeeId) ||
                !string.Equals(authenticatedEmployeeId, actingEmployeeId, StringComparison.OrdinalIgnoreCase))
                return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับพนักงานที่ขอดูข้อมูล");
            if (!await pageAccessService.HasAccess(
                    actingEmployeeId, "LEAVE_PENDING", cancellationToken))
                return StatusCode(StatusCodes.Status403Forbidden,
                    "ไม่มีสิทธิ์เข้าถึงหน้าเอกสารรออนุมัติ");
            var canViewAll = await actionPermissionService.HasPermission(
                    actingEmployeeId, "LEAVE_PENDING", "VIEW_ALL", cancellationToken) ||
                await actionPermissionService.HasPermission(
                    actingEmployeeId, "LEAVE_PENDING", "APPROVE", cancellationToken) ||
                await actionPermissionService.HasPermission(
                    actingEmployeeId, "LEAVE_PENDING", "REJECT", cancellationToken);
            restrictPendingToApprover = !canViewAll;
        }
        else if (string.Equals(status, "EDIT_REQUESTED", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(actingEmployeeId) ||
                !await IsAuthenticatedActor(actingEmployeeId, cancellationToken))
                return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับผู้ดำเนินการ");
            var canViewAll = await actionPermissionService.HasPermission(
                actingEmployeeId, "LEAVE_REVISIONS", "VIEW_ALL", cancellationToken);
            restrictPendingToApprover = !canViewAll;
        }

        const string sql = """
            SELECT d.id, d.document_no, d.creator_employee_id, d.creator_name,
                   d.creator_department,
                   COALESCE(reporting_approver.employee_code, d.approver_employee_id),
                   COALESCE(reporting_approver.full_name, NULLIF(creator_company.leave_approver_name, ''), d.approver_name),
                   t.id, t.code, t.name_th, d.leave_kind, d.leave_date, d.start_time,
                   d.leave_hours, d.leave_reason, d.status, d.created_at,
                   d.has_medical_certificate,
                   COALESCE(@acting_employee_id IN
                       (d.approver_employee_id, reporting_approver.employee_code, upper_reporting_approver.employee_code), FALSE)
            FROM public.leave_documents d
            JOIN public.leave_types t ON t.id = d.leave_type_id
            LEFT JOIN public.employees creator_employee
                   ON creator_employee.employee_code = d.creator_employee_id
                  AND creator_employee.is_active = TRUE
            LEFT JOIN public.employee_company_info creator_company
                   ON creator_company.employee_id = creator_employee.id
            LEFT JOIN LATERAL
            (
                SELECT approver_employee.id AS employee_row_id,
                       approver_employee.employee_code,
                       COALESCE(NULLIF(approver_basic.full_name_th, ''),
                                NULLIF(approver_basic.full_name_en, ''),
                                CONCAT_WS(' ', approver_basic.first_name_th, approver_basic.last_name_th),
                                approver_employee.employee_code) AS full_name
                FROM public.employees approver_employee
                JOIN public.employee_basic_info approver_basic
                  ON approver_basic.employee_id = approver_employee.id
                WHERE approver_employee.is_active = TRUE
                  AND REGEXP_REPLACE(UPPER(BTRIM(COALESCE(creator_company.leave_approver_name, ''))), '\s+', ' ', 'g') IN
                      (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(approver_basic.full_name_th, ''))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(COALESCE(approver_basic.full_name_en, ''))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', approver_basic.first_name_th, approver_basic.last_name_th))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', approver_basic.first_name_en, approver_basic.last_name_en))), '\s+', ' ', 'g'))
                ORDER BY approver_employee.id
                LIMIT 1
            ) reporting_approver ON TRUE
            LEFT JOIN public.employee_company_info reporting_approver_company
                   ON reporting_approver_company.employee_id = reporting_approver.employee_row_id
            LEFT JOIN LATERAL
            (
                SELECT upper_employee.employee_code
                FROM public.employees upper_employee
                JOIN public.employee_basic_info upper_basic
                  ON upper_basic.employee_id = upper_employee.id
                WHERE upper_employee.is_active = TRUE
                  AND REGEXP_REPLACE(UPPER(BTRIM(COALESCE(reporting_approver_company.leave_approver_name, ''))), '\s+', ' ', 'g') IN
                      (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(upper_basic.full_name_th, ''))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(COALESCE(upper_basic.full_name_en, ''))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', upper_basic.first_name_th, upper_basic.last_name_th))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', upper_basic.first_name_en, upper_basic.last_name_en))), '\s+', ' ', 'g'))
                ORDER BY upper_employee.id
                LIMIT 1
            ) upper_reporting_approver ON TRUE
            WHERE (@creator_employee_id IS NULL OR d.creator_employee_id = @creator_employee_id)
              AND (@approver_employee_id IS NULL OR d.approver_employee_id = @approver_employee_id)
              AND (@status IS NULL OR d.status = @status)
              AND (NOT @restrict_pending OR @acting_employee_id IN
                  (d.approver_employee_id, reporting_approver.employee_code, upper_reporting_approver.employee_code))
            ORDER BY d.created_at DESC
            """;

        var result = new List<LeaveDocumentDto>();
        // Keep the command/reader in a dedicated scope. The previous code used
        // `await using var` and also called DisposeAsync explicitly, so the
        // reader could be disposed a second time when the action returned while
        // Npgsql was still consuming the stream.
        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.Add(new NpgsqlParameter<string?>("creator_employee_id", creatorEmployeeId));
            command.Parameters.Add(new NpgsqlParameter<string?>("approver_employee_id", approverEmployeeId));
            command.Parameters.Add(new NpgsqlParameter<string?>("acting_employee_id", actingEmployeeId));
            command.Parameters.Add(new NpgsqlParameter<string?>("status", status));
            command.Parameters.AddWithValue("restrict_pending", restrictPendingToApprover);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(ReadDocument(reader) with { CanCurrentUserReview = reader.GetBoolean(18) });
        }

        for (var index = 0; index < result.Count; index++)
        {
            var pendingCancelRequest = await FindPendingCancelRequest(result[index].Id, cancellationToken);
            if (pendingCancelRequest is not null)
                result[index] = result[index] with { PendingCancelRequest = pendingCancelRequest };
        }

        return Ok(result);
    }

    [HttpGet("{id:long}/attachments")]
    public async Task<ActionResult<IReadOnlyList<LeaveDocumentAttachmentDto>>> GetAttachments(
        long id,
        CancellationToken cancellationToken)
    {
        if (!await CanAccessAttachments(id, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์ดูไฟล์แนบของเอกสารนี้");

        const string sql = """
            SELECT id, original_file_name,
                   COALESCE(NULLIF(content_type, ''), 'application/octet-stream'),
                   file_size_bytes, uploaded_at
            FROM public.leave_document_attachments
            WHERE leave_document_id = @document_id
              AND leave_edit_request_id IS NULL
            ORDER BY uploaded_at, id
            """;
        var result = new List<LeaveDocumentAttachmentDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("document_id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LeaveDocumentAttachmentDto(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return Ok(result);
    }

    [HttpGet("{id:long}/bonus-deduction")]
    public async Task<ActionResult<LeaveBonusDeductionDto>> GetBonusDeduction(
        long id,
        [FromQuery] string actingEmployeeId,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthenticatedActor(actingEmployeeId, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับผู้ดำเนินการ");

        var canView = await actionPermissionService.HasPermission(
                actingEmployeeId, "LEAVE_ALL_DOCUMENTS", "VIEW_BONUS_DEDUCTION", cancellationToken) ||
            await actionPermissionService.HasPermission(
                actingEmployeeId, "LEAVE_ALL_DOCUMENTS", "EDIT_BONUS_DEDUCTION", cancellationToken);
        if (!canView)
            return StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์ดูข้อมูลการหักโบนัส");

        var item = await FindBonusDeduction(id, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPut("{id:long}/bonus-deduction")]
    public async Task<ActionResult<LeaveBonusDeductionDto>> UpdateBonusDeduction(
        long id,
        UpdateLeaveBonusDeductionRequest request,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthenticatedActor(request.ActionBy, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับผู้ดำเนินการ");
        if (!await actionPermissionService.HasPermission(
                request.ActionBy, "LEAVE_ALL_DOCUMENTS", "EDIT_BONUS_DEDUCTION", cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์แก้ไขข้อมูลการหักโบนัส");
        if (request.DeductionPercent is < 0 or > 100)
            return BadRequest("เปอร์เซ็นต์การหักโบนัสต้องอยู่ระหว่าง 0 ถึง 100");
        if (request.IsDeducted && request.IsWaived)
            return BadRequest("ไม่สามารถเลือกหักโบนัสและ Waive พร้อมกันได้");
        if (string.IsNullOrWhiteSpace(request.AdjustmentReason))
            return BadRequest("กรุณาระบุเหตุผลในการปรับข้อมูลการหักโบนัส");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string currentSql = """
            SELECT bonus_deduction_enabled, bonus_deduction_percent,
                   bonus_deduction_waived, bonus_deduction_reason
            FROM public.leave_documents
            WHERE id = @id
            FOR UPDATE
            """;
        bool oldEnabled;
        decimal oldPercent;
        bool oldWaived;
        string? oldReason;
        await using (var command = new NpgsqlCommand(currentSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return NotFound();
            oldEnabled = reader.GetBoolean(0);
            oldPercent = reader.GetDecimal(1);
            oldWaived = reader.GetBoolean(2);
            oldReason = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        var newPercent = request.IsDeducted ? request.DeductionPercent : 0;
        const string updateSql = """
            UPDATE public.leave_documents SET
                bonus_deduction_enabled = @enabled,
                bonus_deduction_percent = @percent,
                bonus_deduction_waived = @waived,
                bonus_deduction_reason = @reason,
                bonus_deduction_overridden = TRUE,
                bonus_deduction_updated_by = @action_by,
                bonus_deduction_updated_by_name = @action_by_name,
                bonus_deduction_updated_at = CURRENT_TIMESTAMP
            WHERE id = @id
            """;
        await using (var command = new NpgsqlCommand(updateSql, connection, transaction))
        {
            command.Parameters.AddWithValue("enabled", request.IsDeducted);
            command.Parameters.AddWithValue("percent", newPercent);
            command.Parameters.AddWithValue("waived", request.IsWaived);
            command.Parameters.AddWithValue("reason", request.AdjustmentReason.Trim());
            command.Parameters.AddWithValue("action_by", request.ActionBy.Trim());
            command.Parameters.AddWithValue("action_by_name", request.ActionByName.Trim());
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var details =
            $"ปรับข้อมูลการหักโบนัส: หักโบนัส {YesNo(oldEnabled)} → {YesNo(request.IsDeducted)}, " +
            $"เปอร์เซ็นต์ {oldPercent:0.##}% → {newPercent:0.##}%, " +
            $"ยกเว้น {YesNo(oldWaived)} → {YesNo(request.IsWaived)}, " +
            $"เหตุผลเดิม: {oldReason ?? "-"}, เหตุผลใหม่: {request.AdjustmentReason.Trim()}";
        await InsertHistory(connection, transaction, id, "BONUS_DEDUCTION_UPDATE", details,
            request.ActionBy.Trim(), request.ActionByName.Trim(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Ok(await FindBonusDeduction(id, cancellationToken));
    }

    [HttpGet("{id:long}/attachments/{attachmentId:long}/preview")]
    public async Task<IActionResult> PreviewAttachment(
        long id,
        long attachmentId,
        CancellationToken cancellationToken)
    {
        if (!await CanAccessAttachments(id, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์ดูไฟล์แนบของเอกสารนี้");

        const string sql = """
            SELECT original_file_name,
                   COALESCE(NULLIF(content_type, ''), 'application/octet-stream'),
                   file_content, storage_path
            FROM public.leave_document_attachments
            WHERE id = @attachment_id AND leave_document_id = @document_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("attachment_id", attachmentId);
        command.Parameters.AddWithValue("document_id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return NotFound("ไม่พบไฟล์แนบ");
        byte[] content;
        if (!reader.IsDBNull(2))
        {
            content = (byte[])reader[2];
        }
        else
        {
            if (reader.IsDBNull(3))
                return NotFound("ไม่พบข้อมูลไฟล์แนบ");
            var attachmentRoot = Path.GetFullPath(Path.Combine(
                environment.ContentRootPath, "App_Data", "leave-attachments"));
            var legacyPath = Path.GetFullPath(Path.Combine(
                environment.ContentRootPath,
                reader.GetString(3).Replace('/', Path.DirectorySeparatorChar)));
            if (!legacyPath.StartsWith(
                    attachmentRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !System.IO.File.Exists(legacyPath))
                return NotFound("ไม่พบไฟล์แนบในพื้นที่จัดเก็บเดิม");
            content = await System.IO.File.ReadAllBytesAsync(legacyPath, cancellationToken);
        }

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        return File(content, reader.GetString(1));
    }

    [HttpGet("{id:long}/edit-requests/pending/attachments")]
    public async Task<ActionResult<IReadOnlyList<LeaveDocumentAttachmentDto>>> GetPendingEditAttachments(
        long id,
        CancellationToken cancellationToken)
    {
        if (!await CanAccessAttachments(id, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์ดูไฟล์แนบของคำขอนี้");

        const string sql = """
            SELECT attachment.id, attachment.original_file_name,
                   COALESCE(NULLIF(attachment.content_type, ''), 'application/octet-stream'),
                   attachment.file_size_bytes, attachment.uploaded_at
            FROM public.leave_document_attachments attachment
            JOIN public.leave_edit_requests request
              ON request.id = attachment.leave_edit_request_id
             AND request.status = 'PENDING'
            WHERE attachment.leave_document_id = @document_id
            ORDER BY attachment.uploaded_at, attachment.id
            """;
        var result = new List<LeaveDocumentAttachmentDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("document_id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LeaveDocumentAttachmentDto(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return Ok(result);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<LeaveDocumentDto>> GetById(long id, CancellationToken cancellationToken)
    {
        var document = await FindDocument(id, cancellationToken);
        if (document is null)
            return NotFound();

        var pendingRequest = await FindPendingEditRequest(id, cancellationToken);
        var pendingCancelRequest = await FindPendingCancelRequest(id, cancellationToken);
        return Ok(document with
        {
            PendingEditRequest = pendingRequest,
            PendingCancelRequest = pendingCancelRequest
        });
    }

    [HttpGet("{id:long}/history")]
    public async Task<ActionResult<IReadOnlyList<LeaveDocumentHistoryDto>>> GetHistory(long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, action, details_text, action_by, action_by_name, action_at
            FROM public.leave_document_history
            WHERE leave_document_id = @document_id
            ORDER BY action_at DESC, id DESC
            """;

        var result = new List<LeaveDocumentHistoryDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("document_id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LeaveDocumentHistoryDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<LeaveDocumentDto>> Create(
        CreateLeaveDocumentRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsValid(request.LeaveTypeId, request.LeaveKind, request.LeaveHours, request.LeaveReason))
            return BadRequest("กรุณากรอกข้อมูลการลาให้ครบถ้วน และระบุจำนวนชั่วโมงระหว่าง 0 ถึง 24 ชั่วโมง");
        var timingError = ValidateLeaveKindDate(request.LeaveKind, request.LeaveDate);
        if (timingError is not null)
            return BadRequest(timingError);
        if (IsRetroactiveBeyondLimit(request.LeaveKind, request.LeaveDate))
            return BadRequest("ลาย้อนหลังได้ไม่เกิน 3 วันปฏิทินนับจากวันที่สร้างเอกสาร");

        if (!await IsAuthenticatedActor(request.CreatorEmployeeId, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับผู้สร้างเอกสาร");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var approver = await ResolveLeaveApprover(connection, request.CreatorEmployeeId, cancellationToken);
        if (approver is null)
            return BadRequest("ไม่พบ Boss หรือ Reporting To (Leave Approve) ของผู้สร้างเอกสาร");
        var leaveType = await FindLeaveType(connection, request.LeaveTypeId, cancellationToken);
        if (leaveType is null)
            return BadRequest("ไม่พบประเภทการลา");
        var certificateError = ValidateMedicalCertificate(
            leaveType.Code, request.HasMedicalCertificate, request.Attachments);
        if (certificateError is not null)
            return BadRequest(certificateError);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var quotaError = await ValidateQuotaAvailability(
            connection, transaction, request.CreatorEmployeeId, request.LeaveTypeId,
            request.LeaveDate.Year, request.LeaveHours, null, cancellationToken);
        if (quotaError is not null)
            return Conflict(quotaError);

        var documentYear = CurrentBuddhistYear();
        var documentGroupNumber = await ReserveNextDocumentGroupNumber(
            connection, transaction, documentYear, cancellationToken);
        var documentNo = FormatDocumentNumber(documentYear, documentGroupNumber, 1);

        const string insertSql = """
            INSERT INTO public.leave_documents
                (document_no, creator_employee_id, creator_name, creator_department,
                 approver_employee_id, approver_name, leave_type_id, leave_kind,
                 leave_date, start_time, leave_hours, leave_reason,
                 has_medical_certificate, status)
            VALUES
                (@temporary_no, @creator_id, @creator_name, @department,
                 @approver_id, @approver_name, @leave_type_id, @leave_kind,
                 @leave_date, @start_time, @hours, @reason,
                 @has_medical_certificate, 'PENDING_APPROVAL')
            RETURNING id
            """;

        long id;
        await using (var command = new NpgsqlCommand(insertSql, connection, transaction))
        {
            // document_no is VARCHAR(30); keep the temporary unique value within that limit.
            command.Parameters.AddWithValue("temporary_no", $"TMP-{Guid.NewGuid():N}"[..30]);
            command.Parameters.AddWithValue("creator_id", request.CreatorEmployeeId);
            command.Parameters.AddWithValue("creator_name", request.CreatorName);
            command.Parameters.AddWithValue("department", request.CreatorDepartment);
            command.Parameters.Add(new NpgsqlParameter<string?>("approver_id", approver.EmployeeId));
            command.Parameters.AddWithValue("approver_name", approver.Name);
            command.Parameters.AddWithValue("leave_type_id", request.LeaveTypeId);
            command.Parameters.AddWithValue("leave_kind", request.LeaveKind);
            command.Parameters.AddWithValue("leave_date", request.LeaveDate);
            command.Parameters.AddWithValue("start_time", request.StartTime);
            command.Parameters.AddWithValue("hours", request.LeaveHours);
            command.Parameters.AddWithValue("reason", request.LeaveReason.Trim());
            command.Parameters.Add(new NpgsqlParameter<bool?>(
                "has_medical_certificate",
                leaveType.Code == "SICK" ? request.HasMedicalCertificate : null));
            id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        await ApplyDefaultBonusDeduction(
            connection, transaction, id, force: true, cancellationToken);

        const string updateNumberSql = "UPDATE public.leave_documents SET document_no = @document_no WHERE id = @id";
        await using (var command = new NpgsqlCommand(updateNumberSql, connection, transaction))
        {
            command.Parameters.AddWithValue("document_no", documentNo);
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var details = $"{LeaveKindText(request.LeaveKind)} วันที่ลา {request.LeaveDate:dd/MM/yyyy} เวลา {request.StartTime:HH\\:mm} จำนวน {request.LeaveHours:0.##} ชั่วโมง เหตุผล: {request.LeaveReason.Trim()}";
        await InsertHistory(connection, transaction, id, "CREATE_DOCUMENT", details,
            request.CreatorEmployeeId, request.CreatorName, cancellationToken);
        foreach (var attachment in request.Attachments ?? [])
        {
            await InsertAttachment(
                connection, transaction, id, attachment,
                request.CreatorEmployeeId, null, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        await SyncCalendarSafely(id);

        await SendApprovalEmailSafely(
            request.CreatorEmployeeId,
            request.CreatorName,
            leaveType.Name,
            [new LeaveApprovalEmailItem(
                id, documentNo, request.LeaveDate, request.StartTime, request.LeaveHours)],
            request.LeaveReason,
            cancellationToken);

        var created = await FindDocument(id, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id }, created);
    }

    [HttpPost("batch")]
    public async Task<ActionResult<IReadOnlyList<LeaveDocumentDto>>> CreateBatch(
        CreateMultiDayLeaveDocumentsRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Items is null ||
            !IsValid(
                request.LeaveTypeId,
                request.LeaveKind,
                request.Items.FirstOrDefault()?.LeaveHours ?? 0,
                request.LeaveReason))
        {
            return BadRequest("กรุณากรอกข้อมูลการลาให้ครบถ้วน");
        }

        if (request.Items.Count == 0)
            return BadRequest("กรุณาสร้างรายการวันที่ลาอย่างน้อย 1 รายการ");
        if (request.Items.Count > 99)
            return BadRequest("จำนวนรายการวันที่ลาต้องไม่เกิน 99 รายการต่อเอกสารหนึ่งชุด");
        if (request.Items.Any(item => item.LeaveHours <= 0 || item.LeaveHours > 24))
            return BadRequest("จำนวนชั่วโมงของแต่ละรายการต้องมากกว่า 0 และไม่เกิน 24 ชั่วโมง");
        if (request.Items.Select(item => item.LeaveDate).Distinct().Count() != request.Items.Count)
            return BadRequest("วันที่ลาในรายการต้องไม่ซ้ำกัน");
        var invalidTimingItem = request.Items.FirstOrDefault(
            item => ValidateLeaveKindDate(request.LeaveKind, item.LeaveDate) is not null);
        if (invalidTimingItem is not null)
            return BadRequest(ValidateLeaveKindDate(request.LeaveKind, invalidTimingItem.LeaveDate));
        if (request.Items.Any(item => IsRetroactiveBeyondLimit(request.LeaveKind, item.LeaveDate)))
            return BadRequest("ลาย้อนหลังได้ไม่เกิน 3 วันปฏิทินนับจากวันที่สร้างเอกสาร กรุณาตรวจสอบวันที่ในรายการ");

        if (!await IsAuthenticatedActor(request.CreatorEmployeeId, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับผู้สร้างเอกสาร");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var approver = await ResolveLeaveApprover(connection, request.CreatorEmployeeId, cancellationToken);
        if (approver is null)
            return BadRequest("ไม่พบ Boss หรือ Reporting To (Leave Approve) ของผู้สร้างเอกสาร");
        var leaveType = await FindLeaveType(connection, request.LeaveTypeId, cancellationToken);
        if (leaveType is null)
            return BadRequest("ไม่พบประเภทการลา");
        var certificateError = ValidateMedicalCertificate(
            leaveType.Code, request.HasMedicalCertificate, request.Attachments);
        if (certificateError is not null)
            return BadRequest(certificateError);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var yearGroup in request.Items.GroupBy(item => item.LeaveDate.Year).OrderBy(group => group.Key))
        {
            var quotaError = await ValidateQuotaAvailability(
                connection, transaction, request.CreatorEmployeeId, request.LeaveTypeId,
                yearGroup.Key, yearGroup.Sum(item => item.LeaveHours), null, cancellationToken);
            if (quotaError is not null)
                return Conflict(quotaError);
        }

        var createdIds = new List<long>(request.Items.Count);
        var documentYear = CurrentBuddhistYear();
        var documentGroupNumber = await ReserveNextDocumentGroupNumber(
            connection, transaction, documentYear, cancellationToken);
        var orderedItems = request.Items.OrderBy(item => item.LeaveDate).ToList();

        const string insertSql = """
            INSERT INTO public.leave_documents
                (document_no, creator_employee_id, creator_name, creator_department,
                 approver_employee_id, approver_name, leave_type_id, leave_kind,
                 leave_date, start_time, leave_hours, leave_reason,
                 has_medical_certificate, status)
            VALUES
                (@temporary_no, @creator_id, @creator_name, @department,
                 @approver_id, @approver_name, @leave_type_id, @leave_kind,
                 @leave_date, @start_time, @hours, @reason,
                 @has_medical_certificate, 'PENDING_APPROVAL')
            RETURNING id
            """;

        for (var itemIndex = 0; itemIndex < orderedItems.Count; itemIndex++)
        {
            var item = orderedItems[itemIndex];
            var leaveDate = item.LeaveDate;
            long id;
            await using (var command = new NpgsqlCommand(insertSql, connection, transaction))
            {
                command.Parameters.AddWithValue("temporary_no", $"TMP-{Guid.NewGuid():N}"[..30]);
                command.Parameters.AddWithValue("creator_id", request.CreatorEmployeeId);
                command.Parameters.AddWithValue("creator_name", request.CreatorName);
                command.Parameters.AddWithValue("department", request.CreatorDepartment);
                command.Parameters.Add(new NpgsqlParameter<string?>("approver_id", approver.EmployeeId));
                command.Parameters.AddWithValue("approver_name", approver.Name);
                command.Parameters.AddWithValue("leave_type_id", request.LeaveTypeId);
                command.Parameters.AddWithValue("leave_kind", request.LeaveKind);
                command.Parameters.AddWithValue("leave_date", leaveDate);
                command.Parameters.AddWithValue("start_time", item.StartTime);
                command.Parameters.AddWithValue("hours", item.LeaveHours);
                command.Parameters.AddWithValue("reason", request.LeaveReason.Trim());
                command.Parameters.Add(new NpgsqlParameter<bool?>(
                    "has_medical_certificate",
                    leaveType.Code == "SICK" ? request.HasMedicalCertificate : null));
                id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            }

            await ApplyDefaultBonusDeduction(
                connection, transaction, id, force: true, cancellationToken);

            var documentNo = FormatDocumentNumber(
                documentYear, documentGroupNumber, itemIndex + 1);
            await using (var command = new NpgsqlCommand(
                "UPDATE public.leave_documents SET document_no = @document_no WHERE id = @id",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("document_no", documentNo);
                command.Parameters.AddWithValue("id", id);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var details =
                $"{LeaveKindText(request.LeaveKind)} วันที่ลา {leaveDate:dd/MM/yyyy} " +
                $"เวลา {item.StartTime:HH\\:mm} จำนวน {item.LeaveHours:0.##} ชั่วโมง " +
                $"เหตุผล: {request.LeaveReason.Trim()}";
            await InsertHistory(
                connection,
                transaction,
                id,
                "CREATE_DOCUMENT",
                details,
                request.CreatorEmployeeId,
                request.CreatorName,
                cancellationToken);
            createdIds.Add(id);
        }

        foreach (var attachment in request.Attachments ?? [])
        {
            foreach (var documentId in createdIds)
            {
                await InsertAttachment(
                    connection, transaction, documentId, attachment,
                    request.CreatorEmployeeId, null, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        foreach (var documentId in createdIds)
            await SyncCalendarSafely(documentId);

        var emailItems = orderedItems.Select((item, index) =>
            new LeaveApprovalEmailItem(
                createdIds[index],
                FormatDocumentNumber(documentYear, documentGroupNumber, index + 1),
                item.LeaveDate,
                item.StartTime,
                item.LeaveHours)).ToList();
        await SendApprovalEmailSafely(
            request.CreatorEmployeeId,
            request.CreatorName,
            leaveType.Name,
            emailItems,
            request.LeaveReason,
            cancellationToken);

        var createdDocuments = new List<LeaveDocumentDto>(createdIds.Count);
        foreach (var id in createdIds)
        {
            var document = await FindDocument(id, cancellationToken);
            if (document is not null)
                createdDocuments.Add(document);
        }

        return StatusCode(StatusCodes.Status201Created, createdDocuments);
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, UpdateLeaveDocumentRequest request, CancellationToken cancellationToken)
    {
        if (!IsValid(request.LeaveTypeId, request.LeaveKind, request.LeaveHours, request.LeaveReason))
            return BadRequest("ข้อมูลไม่ครบถ้วน");
        var timingError = ValidateLeaveKindDate(request.LeaveKind, request.LeaveDate);
        if (timingError is not null)
            return BadRequest(timingError);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string currentSql = """
            SELECT d.leave_type_id, current_type.name_th, d.leave_kind, d.leave_date,
                   d.start_time, d.leave_hours, d.leave_reason, requested_type.name_th,
                   d.creator_employee_id
            FROM public.leave_documents d
            JOIN public.leave_types current_type ON current_type.id = d.leave_type_id
            JOIN public.leave_types requested_type ON requested_type.id = @requested_type_id
            WHERE d.id = @id AND d.status = 'PENDING_APPROVAL'
              AND NOT EXISTS
                  (SELECT 1 FROM public.leave_cancel_requests c
                   WHERE c.leave_document_id = d.id AND c.status = 'PENDING')
            FOR UPDATE OF d
            """;

        long currentTypeId;
        string currentTypeName;
        string currentKind;
        DateOnly currentDate;
        TimeOnly currentStartTime;
        decimal currentHours;
        string currentReason;
        string requestedTypeName;
        string creatorEmployeeId;
        await using (var currentCommand = new NpgsqlCommand(currentSql, connection, transaction))
        {
            currentCommand.Parameters.AddWithValue("requested_type_id", request.LeaveTypeId);
            currentCommand.Parameters.AddWithValue("id", id);
            await using var reader = await currentCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return Conflict("แก้ไขได้เฉพาะเอกสารสถานะรออนุมัติ");

            currentTypeId = reader.GetInt64(0);
            currentTypeName = reader.GetString(1);
            currentKind = reader.GetString(2);
            currentDate = reader.GetFieldValue<DateOnly>(3);
            currentStartTime = reader.GetFieldValue<TimeOnly>(4);
            currentHours = reader.GetDecimal(5);
            currentReason = reader.GetString(6);
            requestedTypeName = reader.GetString(7);
            creatorEmployeeId = reader.GetString(8);
        }

        if (BangkokNow() >= currentDate.ToDateTime(currentStartTime) &&
            !string.Equals(currentKind, "RETROACTIVE", StringComparison.OrdinalIgnoreCase))
            return Conflict("เอกสารนี้ถึงวันเวลาเริ่มลาแล้ว จึงไม่สามารถแก้ไขได้");

        var quotaError = await ValidateQuotaAvailability(
            connection, transaction, creatorEmployeeId, request.LeaveTypeId,
            request.LeaveDate.Year, request.LeaveHours, id, cancellationToken);
        if (quotaError is not null)
            return Conflict(quotaError);

        const string sql = """
            UPDATE public.leave_documents
            SET leave_type_id = @leave_type_id, leave_kind = @leave_kind, leave_date = @leave_date,
                start_time = @start_time, leave_hours = @hours, leave_reason = @reason
            WHERE id = @id AND status = 'PENDING_APPROVAL'
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("leave_type_id", request.LeaveTypeId);
        command.Parameters.AddWithValue("leave_kind", request.LeaveKind);
        command.Parameters.AddWithValue("leave_date", request.LeaveDate);
        command.Parameters.AddWithValue("start_time", request.StartTime);
        command.Parameters.AddWithValue("hours", request.LeaveHours);
        command.Parameters.AddWithValue("reason", request.LeaveReason.Trim());
        command.Parameters.AddWithValue("id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            return Conflict("แก้ไขได้เฉพาะเอกสารสถานะรออนุมัติ");

        if (currentTypeId != request.LeaveTypeId)
        {
            await ApplyDefaultBonusDeduction(
                connection, transaction, id, force: false, cancellationToken);
        }

        var changedFields = DescribeChangedFields(
            currentTypeId, currentTypeName, currentKind, currentDate, currentStartTime, currentHours,
            request.LeaveTypeId, requestedTypeName, currentKind, request.LeaveDate, request.StartTime,
            request.LeaveHours);
        if (!string.Equals(currentReason, request.LeaveReason.Trim(), StringComparison.Ordinal))
            changedFields.Add($"เหตุผลการลา \"{currentReason}\" → \"{request.LeaveReason.Trim()}\"");
        var details = changedFields.Count == 0
            ? "บันทึกเอกสารโดยไม่มีข้อมูลเปลี่ยนแปลง"
            : $"แก้ไข: {string.Join(", ", changedFields)}";
        await InsertHistory(connection, transaction, id, "EDIT", details, request.ActionBy, request.ActionByName, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await SyncCalendarSafely(id);
        return NoContent();
    }

    [HttpPost("{id:long}/approve")]
    public async Task<IActionResult> Approve(
        long id,
        ReviewLeaveDocumentRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActionBy) ||
            string.IsNullOrWhiteSpace(request.ActionByName))
        {
            return BadRequest("ไม่พบข้อมูลผู้อนุมัติ");
        }

        if (!await IsAuthenticatedActor(request.ActionBy, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับผู้ดำเนินการ");

        var permissionError = await ValidateReviewer(
            id, request.ActionBy, "APPROVE", cancellationToken);
        if (permissionError is not null)
            return permissionError;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string sql = """
            UPDATE public.leave_documents
            SET status = 'APPROVED',
                approved_at = CURRENT_TIMESTAMP,
                rejected_at = NULL
            WHERE id = @id AND status = 'PENDING_APPROVAL'
            """;

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                return Conflict("อนุมัติได้เฉพาะเอกสารที่อยู่ในสถานะรออนุมัติ");
        }

        var details = string.IsNullOrWhiteSpace(request.Remark)
            ? "อนุมัติเอกสารการลา"
            : $"อนุมัติเอกสารการลา หมายเหตุ: {request.Remark.Trim()}";
        await InsertHistory(
            connection,
            transaction,
            id,
            "APPROVE",
            details,
            request.ActionBy,
            request.ActionByName,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await SyncCalendarSafely(id);
        await SendDecisionEmailSafely(
            id, true, request.ActionBy, request.ActionByName, request.Remark);
        return NoContent();
    }

    [HttpPost("{id:long}/reject")]
    public async Task<IActionResult> Reject(
        long id,
        ReviewLeaveDocumentRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActionBy) ||
            string.IsNullOrWhiteSpace(request.ActionByName))
        {
            return BadRequest("ไม่พบข้อมูลผู้ไม่อนุมัติ");
        }

        if (!await IsAuthenticatedActor(request.ActionBy, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับผู้ดำเนินการ");

        var permissionError = await ValidateReviewer(
            id, request.ActionBy, "REJECT", cancellationToken);
        if (permissionError is not null)
            return permissionError;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string sql = """
            UPDATE public.leave_documents
            SET status = 'REJECTED',
                rejected_at = CURRENT_TIMESTAMP,
                approved_at = NULL
            WHERE id = @id AND status = 'PENDING_APPROVAL'
            """;

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                return Conflict("ไม่อนุมัติได้เฉพาะเอกสารที่อยู่ในสถานะรออนุมัติ");
        }

        var details = string.IsNullOrWhiteSpace(request.Remark)
            ? "ไม่อนุมัติเอกสารการลา"
            : $"ไม่อนุมัติเอกสารการลา เหตุผล: {request.Remark.Trim()}";
        await InsertHistory(
            connection,
            transaction,
            id,
            "REJECT",
            details,
            request.ActionBy,
            request.ActionByName,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await SyncCalendarSafely(id);
        await SendDecisionEmailSafely(
            id, false, request.ActionBy, request.ActionByName, request.Remark);
        return NoContent();
    }

    [HttpPost("{id:long}/cancel")]
    public async Task<IActionResult> CancelBeforeLeaveStarts(
        long id,
        ReviewLeaveDocumentRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActionBy) ||
            string.IsNullOrWhiteSpace(request.ActionByName) ||
            !await IsAuthenticatedActor(request.ActionBy, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับผู้ดำเนินการ");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            UPDATE public.leave_documents d
            SET status = 'CANCELLED', cancelled_at = CURRENT_TIMESTAMP
            WHERE d.id = @id
              AND d.creator_employee_id = @action_by
              AND d.status IN ('PENDING_APPROVAL', 'APPROVED')
              AND (d.leave_date + d.start_time) >
                  (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Bangkok')
              AND NOT EXISTS
                  (SELECT 1 FROM public.leave_edit_requests e
                   WHERE e.leave_document_id = d.id AND e.status = 'PENDING')
              AND NOT EXISTS
                  (SELECT 1 FROM public.leave_cancel_requests c
                   WHERE c.leave_document_id = d.id AND c.status = 'PENDING')
            RETURNING d.document_no
            """;
        string? documentNo;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("action_by", request.ActionBy.Trim());
            documentNo = (string?)await command.ExecuteScalarAsync(cancellationToken);
        }
        if (documentNo is null)
            return Conflict("ยกเลิกทันทีได้เฉพาะเอกสารที่ยังไม่ถึงวันเวลาเริ่มลาและไม่มีคำขออื่นรอดำเนินการ");

        await InsertHistory(connection, transaction, id, "CANCEL",
            $"ยกเลิกเอกสาร {documentNo} ก่อนถึงวันเวลาเริ่มลา",
            request.ActionBy, request.ActionByName, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await SyncCalendarSafely(id);
        await SendCancellationEmailSafely(
            id,
            request.ActionByName,
            request.Remark);
        return NoContent();
    }

    [HttpPost("{id:long}/cancel-requests")]
    public async Task<IActionResult> RequestCancellation(
        long id,
        ReviewLeaveDocumentRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActionBy) ||
            string.IsNullOrWhiteSpace(request.ActionByName) ||
            string.IsNullOrWhiteSpace(request.Remark))
        {
            return BadRequest("กรุณาระบุผู้ขอและเหตุผลในการขอยกเลิกเอกสาร");
        }

        if (!await IsAuthenticatedActor(request.ActionBy, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับผู้ดำเนินการ");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string documentSql = """
            SELECT document_no
            FROM public.leave_documents d
            WHERE d.id = @id
              AND d.creator_employee_id = @requested_by
              AND d.status = 'APPROVED'
              AND (d.leave_date + d.start_time) <=
                  (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Bangkok')
              AND NOT EXISTS
                  (SELECT 1 FROM public.leave_edit_requests e
                   WHERE e.leave_document_id = d.id AND e.status = 'PENDING')
            FOR UPDATE OF d
            """;

        string? documentNo;
        await using (var command = new NpgsqlCommand(documentSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("requested_by", request.ActionBy.Trim());
            documentNo = (string?)await command.ExecuteScalarAsync(cancellationToken);
        }

        if (documentNo is null)
            return Conflict(
                "ขอยกเลิกได้เฉพาะเอกสารที่อนุมัติแล้วและถึงวันเวลาเริ่มลาแล้ว โดยต้องไม่มีคำขออื่นรอดำเนินการ");

        const string requestSql = """
            INSERT INTO public.leave_cancel_requests
                (leave_document_id, request_reason, requested_by, requested_by_name)
            VALUES (@document_id, @reason, @requested_by, @requested_by_name)
            ON CONFLICT (leave_document_id) WHERE status = 'PENDING'
            DO NOTHING
            RETURNING id
            """;
        await using (var command = new NpgsqlCommand(requestSql, connection, transaction))
        {
            command.Parameters.AddWithValue("document_id", id);
            command.Parameters.AddWithValue("reason", request.Remark.Trim());
            command.Parameters.AddWithValue("requested_by", request.ActionBy.Trim());
            command.Parameters.AddWithValue("requested_by_name", request.ActionByName.Trim());
            if (await command.ExecuteScalarAsync(cancellationToken) is null)
                return Conflict("เอกสารนี้มีคำขอยกเลิกที่รอดำเนินการอยู่แล้ว");
        }

        var details = $"ขอยกเลิกเอกสาร {documentNo}; เหตุผล: {request.Remark.Trim()}";
        await InsertHistory(
            connection,
            transaction,
            id,
            "REQUEST_CANCEL",
            details,
            request.ActionBy,
            request.ActionByName,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:long}/edit-requests")]
    public async Task<IActionResult> SaveEditRequest(long id, SaveLeaveEditRequest request, CancellationToken cancellationToken)
    {
        if (!IsValid(request.LeaveTypeId, request.LeaveKind, request.LeaveHours, request.RequestReason))
            return BadRequest("กรุณากรอกข้อมูลและเหตุผลในการขอแก้ไขให้ครบถ้วน");
        var attachmentError = ValidateAttachments(request.Attachments);
        if (attachmentError is not null)
            return BadRequest(attachmentError);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string lockSql = """
            SELECT d.status, d.leave_type_id, current_type.name_th, d.leave_kind,
                   d.leave_date, d.start_time, d.leave_hours,
                   requested_type.name_th, d.has_medical_certificate,
                   requested_type.code,
                   (SELECT COUNT(*)
                    FROM public.leave_document_attachments a
                    JOIN public.leave_edit_requests existing_request
                      ON existing_request.id = a.leave_edit_request_id
                     AND existing_request.status = 'PENDING'
                    WHERE a.leave_document_id = d.id),
                   EXISTS
                       (SELECT 1 FROM public.leave_cancel_requests c
                        WHERE c.leave_document_id = d.id AND c.status = 'PENDING'),
                   d.creator_employee_id
            FROM public.leave_documents d
            JOIN public.leave_types current_type ON current_type.id = d.leave_type_id
            JOIN public.leave_types requested_type ON requested_type.id = @requested_type_id
            WHERE d.id = @id
            FOR UPDATE OF d
            """;
        string? documentStatus;
        long currentTypeId;
        string currentTypeName;
        string currentKind;
        DateOnly currentDate;
        TimeOnly currentStartTime;
        decimal currentHours;
        string requestedTypeName;
        bool? currentHasMedicalCertificate;
        string requestedTypeCode;
        long pendingAttachmentCount;
        bool hasPendingCancelRequest;
        string creatorEmployeeId;
        await using (var command = new NpgsqlCommand(lockSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("requested_type_id", request.LeaveTypeId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return NotFound("ไม่พบเอกสารหรือประเภทการลาที่ต้องการ");

            documentStatus = reader.GetString(0);
            currentTypeId = reader.GetInt64(1);
            currentTypeName = reader.GetString(2);
            currentKind = reader.GetString(3);
            currentDate = reader.GetFieldValue<DateOnly>(4);
            currentStartTime = reader.GetFieldValue<TimeOnly>(5);
            currentHours = reader.GetDecimal(6);
            requestedTypeName = reader.GetString(7);
            currentHasMedicalCertificate = reader.IsDBNull(8) ? null : reader.GetBoolean(8);
            requestedTypeCode = reader.GetString(9);
            pendingAttachmentCount = reader.GetInt64(10);
            hasPendingCancelRequest = reader.GetBoolean(11);
            creatorEmployeeId = reader.GetString(12);
        }

        if (documentStatus is not ("APPROVED" or "EDIT_REQUESTED"))
            return Conflict("ขอแก้ไขได้เฉพาะเอกสารที่อนุมัติแล้วหรือมีคำขอแก้ไขอยู่");

        if (hasPendingCancelRequest)
            return Conflict("เอกสารนี้มีคำขอยกเลิกที่รอดำเนินการอยู่ จึงไม่สามารถขอแก้ไขได้");

        var quotaError = await ValidateQuotaAvailability(
            connection, transaction, creatorEmployeeId, request.LeaveTypeId,
            request.LeaveDate.Year, request.LeaveHours, id, cancellationToken);
        if (quotaError is not null)
            return Conflict(quotaError);

        if (string.Equals(requestedTypeCode, "SICK", StringComparison.OrdinalIgnoreCase))
        {
            if (!request.HasMedicalCertificate.HasValue)
                return BadRequest("กรุณาระบุว่ามีใบรับรองแพทย์หรือไม่");
            if (request.HasMedicalCertificate == true &&
                currentHasMedicalCertificate != true &&
                (request.Attachments is null || request.Attachments.Count == 0) &&
                pendingAttachmentCount == 0)
                return BadRequest("กรุณาแนบใบรับรองแพทย์");
        }

        const string upsertSql = """
            INSERT INTO public.leave_edit_requests
                (leave_document_id, requested_leave_type_id, requested_leave_kind,
                 requested_leave_date, requested_start_time, requested_leave_hours,
                 requested_has_medical_certificate, request_reason,
                 requested_by, requested_by_name)
            VALUES
                (@document_id, @leave_type_id, @leave_kind, @leave_date, @start_time, @hours,
                 @has_medical_certificate, @reason, @requested_by, @requested_by_name)
            ON CONFLICT (leave_document_id) WHERE status = 'PENDING'
            DO UPDATE SET
                requested_leave_type_id = EXCLUDED.requested_leave_type_id,
                requested_leave_kind = EXCLUDED.requested_leave_kind,
                requested_leave_date = EXCLUDED.requested_leave_date,
                requested_start_time = EXCLUDED.requested_start_time,
                requested_leave_hours = EXCLUDED.requested_leave_hours,
                requested_has_medical_certificate = EXCLUDED.requested_has_medical_certificate,
                request_reason = EXCLUDED.request_reason,
                requested_by = EXCLUDED.requested_by,
                requested_by_name = EXCLUDED.requested_by_name
            RETURNING id
            """;
        long editRequestId;
        await using (var command = new NpgsqlCommand(upsertSql, connection, transaction))
        {
            command.Parameters.AddWithValue("document_id", id);
            command.Parameters.AddWithValue("leave_type_id", request.LeaveTypeId);
            // Leave kind (advance/retroactive) is fixed when the document is
            // created and cannot be changed through an edit request.
            command.Parameters.AddWithValue("leave_kind", currentKind);
            command.Parameters.AddWithValue("leave_date", request.LeaveDate);
            command.Parameters.AddWithValue("start_time", request.StartTime);
            command.Parameters.AddWithValue("hours", request.LeaveHours);
            command.Parameters.Add(new NpgsqlParameter<bool?>(
                "has_medical_certificate",
                string.Equals(requestedTypeCode, "SICK", StringComparison.OrdinalIgnoreCase)
                    ? request.HasMedicalCertificate
                    : null));
            command.Parameters.AddWithValue("reason", request.RequestReason.Trim());
            command.Parameters.AddWithValue("requested_by", request.RequestedBy);
            command.Parameters.AddWithValue("requested_by_name", request.RequestedByName);
            editRequestId = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        foreach (var attachment in request.Attachments ?? [])
        {
            await InsertAttachment(
                connection, transaction, id, attachment,
                request.RequestedBy, editRequestId, cancellationToken);
        }

        await using (var command = new NpgsqlCommand("UPDATE public.leave_documents SET status = 'EDIT_REQUESTED' WHERE id = @id", connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var action = documentStatus == "APPROVED" ? "REQUEST_EDIT" : "EDIT";
        var changedFields = DescribeChangedFields(
            currentTypeId, currentTypeName, currentKind, currentDate, currentStartTime, currentHours,
            request.LeaveTypeId, requestedTypeName, currentKind, request.LeaveDate, request.StartTime,
            request.LeaveHours);
        if (string.Equals(requestedTypeCode, "SICK", StringComparison.OrdinalIgnoreCase) &&
            currentHasMedicalCertificate != request.HasMedicalCertificate)
        {
            changedFields.Add($"ใบรับรองแพทย์จาก {MedicalCertificateText(currentHasMedicalCertificate)} เป็น {MedicalCertificateText(request.HasMedicalCertificate)}");
        }
        var changedDetails = changedFields.Count == 0
            ? "ไม่มีฟิลด์ข้อมูลการลาเปลี่ยนแปลง"
            : string.Join(", ", changedFields);
        var addedFileDetails = request.Attachments is { Count: > 0 }
            ? $"; ขอเพิ่มไฟล์แนบ {request.Attachments.Count} ไฟล์"
            : string.Empty;
        var details = $"ขอแก้ไข: {changedDetails}{addedFileDetails}; เหตุผล: {request.RequestReason.Trim()}";
        await InsertHistory(connection, transaction, id, action, details, request.RequestedBy, request.RequestedByName, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("cancel-requests/pending")]
    public async Task<ActionResult<IReadOnlyList<LeaveDocumentDto>>> GetPendingCancelRequests(
        [FromQuery] string actingEmployeeId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actingEmployeeId) ||
            !await IsAuthenticatedActor(actingEmployeeId, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับผู้ดำเนินการ");

        const string sql = """
            SELECT leave_document_id
            FROM public.leave_cancel_requests
            WHERE status = 'PENDING'
            ORDER BY requested_at DESC
            """;
        var documentIds = new List<long>();
        await using (var command = dataSource.CreateCommand(sql))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                documentIds.Add(reader.GetInt64(0));
        }

        var result = new List<LeaveDocumentDto>(documentIds.Count);
        foreach (var documentId in documentIds)
        {
            if (await ValidateReviewer(
                    documentId, actingEmployeeId, "VIEW_ALL", cancellationToken,
                    "LEAVE_REVISIONS") is not null)
                continue;
            var document = await FindDocument(documentId, cancellationToken);
            var cancelRequest = await FindPendingCancelRequest(documentId, cancellationToken);
            if (document is not null && cancelRequest is not null)
                result.Add(document with { PendingCancelRequest = cancelRequest });
        }
        return Ok(result);
    }

    [HttpDelete("{id:long}/cancel-requests/pending")]
    public async Task<IActionResult> CancelPendingCancelRequest(
        long id,
        [FromQuery] string actionBy,
        [FromQuery] string actionByName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actionBy) || string.IsNullOrWhiteSpace(actionByName) ||
            !await IsAuthenticatedActor(actionBy, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับผู้ดำเนินการ");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            UPDATE public.leave_cancel_requests r
            SET status = 'CANCELLED', reviewed_at = CURRENT_TIMESTAMP
            FROM public.leave_documents d
            WHERE r.leave_document_id = @document_id
              AND r.status = 'PENDING'
              AND r.requested_by = @action_by
              AND d.id = r.leave_document_id
            """;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("document_id", id);
            command.Parameters.AddWithValue("action_by", actionBy.Trim());
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                return Conflict("ไม่พบคำขอยกเลิกที่รอดำเนินการ หรือผู้ใช้งานไม่ใช่เจ้าของคำขอ");
        }

        await InsertHistory(connection, transaction, id, "CANCEL_CANCEL_REQUEST",
            "ยกเลิกคำขอยกเลิกเอกสาร", actionBy, actionByName, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:long}/cancel-requests/pending/approve")]
    public Task<IActionResult> ApproveCancelRequest(
        long id, ReviewLeaveDocumentRequest request, CancellationToken cancellationToken) =>
        ReviewPendingCancelRequest(id, request, true, cancellationToken);

    [HttpPost("{id:long}/cancel-requests/pending/reject")]
    public Task<IActionResult> RejectCancelRequest(
        long id, ReviewLeaveDocumentRequest request, CancellationToken cancellationToken) =>
        ReviewPendingCancelRequest(id, request, false, cancellationToken);

    [HttpDelete("{id:long}/edit-requests/pending")]
    public async Task<IActionResult> CancelEditRequest(
        long id,
        [FromQuery] string actionBy,
        [FromQuery] string actionByName,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string cancelSql = """
            UPDATE public.leave_edit_requests r
            SET status = 'CANCELLED', reviewed_at = CURRENT_TIMESTAMP
            FROM public.leave_documents d
            WHERE r.leave_document_id = @document_id
              AND r.status = 'PENDING'
              AND d.id = r.leave_document_id
            """;
        await using (var command = new NpgsqlCommand(cancelSql, connection, transaction))
        {
            command.Parameters.AddWithValue("document_id", id);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                return Conflict("ไม่พบคำขอแก้ไขที่รอดำเนินการ");
        }

        await using (var command = new NpgsqlCommand("UPDATE public.leave_documents SET status = 'APPROVED' WHERE id = @id", connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertHistory(connection, transaction, id, "CANCEL_EDIT_REQUEST", "ยกเลิกคำขอแก้ไข โดยข้อมูลเอกสารหลักไม่เปลี่ยนแปลง", actionBy, actionByName, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:long}/edit-requests/pending/approve")]
    public async Task<IActionResult> ApproveEditRequest(
        long id,
        ReviewLeaveDocumentRequest request,
        CancellationToken cancellationToken)
    {
        return await ReviewPendingEditRequest(id, request, true, cancellationToken);
    }

    [HttpPost("{id:long}/edit-requests/pending/reject")]
    public async Task<IActionResult> RejectEditRequest(
        long id,
        ReviewLeaveDocumentRequest request,
        CancellationToken cancellationToken)
    {
        return await ReviewPendingEditRequest(id, request, false, cancellationToken);
    }

    private async Task<IActionResult?> ValidateReviewer(
        long documentId,
        string actorEmployeeId,
        string actionKey,
        CancellationToken cancellationToken,
        string permissionPageCode = "LEAVE_PENDING")
    {
        const string sql = """
            SELECT reporting_approver.employee_code,
                   upper_reporting_approver.employee_code,
                   document.approver_employee_id
            FROM public.leave_documents document
            LEFT JOIN public.employees creator_employee
                   ON creator_employee.employee_code = document.creator_employee_id
                  AND creator_employee.is_active = TRUE
            LEFT JOIN public.employee_company_info creator_company
                   ON creator_company.employee_id = creator_employee.id
            LEFT JOIN LATERAL
            (
                SELECT approver_employee.id AS employee_row_id,
                       approver_employee.employee_code
                FROM public.employees approver_employee
                JOIN public.employee_basic_info approver_basic
                  ON approver_basic.employee_id = approver_employee.id
                WHERE approver_employee.is_active = TRUE
                  AND REGEXP_REPLACE(UPPER(BTRIM(COALESCE(creator_company.leave_approver_name, ''))), '\s+', ' ', 'g') IN
                      (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(approver_basic.full_name_th, ''))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(COALESCE(approver_basic.full_name_en, ''))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', approver_basic.first_name_th, approver_basic.last_name_th))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', approver_basic.first_name_en, approver_basic.last_name_en))), '\s+', ' ', 'g'))
                ORDER BY approver_employee.id
                LIMIT 1
            ) reporting_approver ON TRUE
            LEFT JOIN public.employee_company_info reporting_approver_company
                   ON reporting_approver_company.employee_id = reporting_approver.employee_row_id
            LEFT JOIN LATERAL
            (
                SELECT upper_employee.employee_code
                FROM public.employees upper_employee
                JOIN public.employee_basic_info upper_basic
                  ON upper_basic.employee_id = upper_employee.id
                WHERE upper_employee.is_active = TRUE
                  AND REGEXP_REPLACE(UPPER(BTRIM(COALESCE(reporting_approver_company.leave_approver_name, ''))), '\s+', ' ', 'g') IN
                      (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(upper_basic.full_name_th, ''))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(COALESCE(upper_basic.full_name_en, ''))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', upper_basic.first_name_th, upper_basic.last_name_th))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', upper_basic.first_name_en, upper_basic.last_name_en))), '\s+', ' ', 'g'))
                ORDER BY upper_employee.id
                LIMIT 1
            ) upper_reporting_approver ON TRUE
            WHERE document.id = @id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return NotFound("ไม่พบเอกสารการลา");
        var reportingApprover = reader.IsDBNull(0) ? null : reader.GetString(0);
        var upperReportingApprover = reader.IsDBNull(1) ? null : reader.GetString(1);
        var storedApprover = reader.IsDBNull(2) ? null : reader.GetString(2);
        if (string.Equals(reportingApprover, actorEmployeeId.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(upperReportingApprover, actorEmployeeId.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(storedApprover, actorEmployeeId.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;
        if (await actionPermissionService.HasPermission(
                actorEmployeeId, permissionPageCode, actionKey, cancellationToken))
            return null;
        return StatusCode(
            StatusCodes.Status403Forbidden,
            $"ไม่มีสิทธิ์{(actionKey.StartsWith("APPROVE", StringComparison.OrdinalIgnoreCase) ? "อนุมัติ" : "ไม่อนุมัติ")}เอกสารนี้");
    }

    private async Task<bool> IsAuthenticatedActor(
        string actorEmployeeId,
        CancellationToken cancellationToken)
    {
        var authenticatedEmployeeId = await ResolveAuthenticatedEmployeeId(cancellationToken);
        return !string.IsNullOrWhiteSpace(authenticatedEmployeeId) &&
            string.Equals(authenticatedEmployeeId, actorEmployeeId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> CanAccessAttachments(long documentId, CancellationToken cancellationToken)
    {
        var authenticatedEmployeeId = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(authenticatedEmployeeId))
            return false;

        const string ownerSql = """
            SELECT creator_employee_id
            FROM public.leave_documents
            WHERE id = @id
            """;
        await using (var command = dataSource.CreateCommand(ownerSql))
        {
            command.Parameters.AddWithValue("id", documentId);
            var ownerEmployeeId = (string?)await command.ExecuteScalarAsync(cancellationToken);
            if (string.Equals(ownerEmployeeId, authenticatedEmployeeId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return await ValidateReviewer(
            documentId, authenticatedEmployeeId, "APPROVE", cancellationToken) is null;
    }

    private async Task<string?> ResolveAuthenticatedEmployeeId(CancellationToken cancellationToken)
    {
        var tenantId = User.FindFirst("tid")?.Value;
        var objectId = User.FindFirst("oid")?.Value;
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
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<LeaveDocumentDto?> FindDocument(long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.id, d.document_no, d.creator_employee_id, d.creator_name,
                   d.creator_department,
                   COALESCE(reporting_approver.employee_code, d.approver_employee_id),
                   COALESCE(reporting_approver.full_name, NULLIF(creator_company.leave_approver_name, ''), d.approver_name),
                   t.id, t.code, t.name_th, d.leave_kind, d.leave_date, d.start_time,
                   d.leave_hours, d.leave_reason, d.status, d.created_at,
                   d.has_medical_certificate
            FROM public.leave_documents d
            JOIN public.leave_types t ON t.id = d.leave_type_id
            LEFT JOIN public.employees creator_employee
                   ON creator_employee.employee_code = d.creator_employee_id
                  AND creator_employee.is_active = TRUE
            LEFT JOIN public.employee_company_info creator_company
                   ON creator_company.employee_id = creator_employee.id
            LEFT JOIN LATERAL
            (
                SELECT approver_employee.employee_code,
                       COALESCE(NULLIF(approver_basic.full_name_th, ''),
                                NULLIF(approver_basic.full_name_en, ''),
                                CONCAT_WS(' ', approver_basic.first_name_th, approver_basic.last_name_th),
                                approver_employee.employee_code) AS full_name
                FROM public.employees approver_employee
                JOIN public.employee_basic_info approver_basic
                  ON approver_basic.employee_id = approver_employee.id
                WHERE approver_employee.is_active = TRUE
                  AND REGEXP_REPLACE(UPPER(BTRIM(COALESCE(creator_company.leave_approver_name, ''))), '\s+', ' ', 'g') IN
                      (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(approver_basic.full_name_th, ''))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(COALESCE(approver_basic.full_name_en, ''))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', approver_basic.first_name_th, approver_basic.last_name_th))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', approver_basic.first_name_en, approver_basic.last_name_en))), '\s+', ' ', 'g'))
                ORDER BY approver_employee.id
                LIMIT 1
            ) reporting_approver ON TRUE
            WHERE d.id = @id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDocument(reader) : null;
    }

    private async Task<LeaveEditRequestDto?> FindPendingEditRequest(long documentId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT r.id, r.leave_document_id, r.requested_leave_type_id,
                   t.name_th, r.requested_leave_kind, r.requested_leave_date,
                   r.requested_start_time, r.requested_leave_hours,
                   r.requested_has_medical_certificate,
                   r.request_reason, r.status, r.requested_by,
                   r.requested_by_name, r.requested_at
            FROM public.leave_edit_requests r
            JOIN public.leave_types t ON t.id = r.requested_leave_type_id
            WHERE r.leave_document_id = @document_id AND r.status = 'PENDING'
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("document_id", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new LeaveEditRequestDto(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateOnly>(5),
            reader.GetFieldValue<TimeOnly>(6), reader.GetDecimal(7),
            reader.IsDBNull(8) ? null : reader.GetBoolean(8), reader.GetString(9),
            reader.GetString(10), reader.GetString(11), reader.GetString(12),
            reader.GetFieldValue<DateTimeOffset>(13));
    }

    private async Task<LeaveCancelRequestDto?> FindPendingCancelRequest(
        long documentId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, leave_document_id, request_reason, status,
                   requested_by, requested_by_name, requested_at
            FROM public.leave_cancel_requests
            WHERE leave_document_id = @document_id AND status = 'PENDING'
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("document_id", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new LeaveCancelRequestDto(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6));
    }

    private async Task<IActionResult> ReviewPendingEditRequest(
        long documentId,
        ReviewLeaveDocumentRequest review,
        bool approve,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(review.ActionBy) ||
            string.IsNullOrWhiteSpace(review.ActionByName))
        {
            return BadRequest("ไม่พบข้อมูลผู้ตรวจสอบคำขอแก้ไข");
        }

        if (!await IsAuthenticatedActor(review.ActionBy, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับผู้ดำเนินการ");
        var permissionError = await ValidateReviewer(
            documentId, review.ActionBy,
            approve ? "APPROVE_EDIT" : "REJECT_EDIT",
            cancellationToken, "LEAVE_REVISIONS");
        if (permissionError is not null)
            return permissionError;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string selectSql = """
            SELECT r.id,
                   d.leave_type_id, current_type.name_th,
                   d.leave_kind, d.leave_date, d.start_time, d.leave_hours,
                   r.requested_leave_type_id, requested_type.name_th,
                   r.requested_leave_kind, r.requested_leave_date, r.requested_start_time,
                   r.requested_leave_hours, r.request_reason,
                   d.has_medical_certificate, r.requested_has_medical_certificate,
                   d.creator_employee_id
            FROM public.leave_documents d
            JOIN public.leave_edit_requests r
              ON r.leave_document_id = d.id AND r.status = 'PENDING'
            JOIN public.leave_types current_type ON current_type.id = d.leave_type_id
            JOIN public.leave_types requested_type ON requested_type.id = r.requested_leave_type_id
            WHERE d.id = @document_id AND d.status = 'EDIT_REQUESTED'
            FOR UPDATE OF d, r
            """;

        long requestId;
        long currentTypeId;
        string currentTypeName;
        string currentKind;
        DateOnly currentDate;
        TimeOnly currentStartTime;
        decimal currentHours;
        long requestedTypeId;
        string requestedTypeName;
        string requestedKind;
        DateOnly requestedDate;
        TimeOnly requestedStartTime;
        decimal requestedHours;
        string requestReason;
        bool? currentHasMedicalCertificate;
        bool? requestedHasMedicalCertificate;
        string creatorEmployeeId;

        await using (var command = new NpgsqlCommand(selectSql, connection, transaction))
        {
            command.Parameters.AddWithValue("document_id", documentId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return Conflict("ไม่พบคำขอแก้ไขที่อยู่ระหว่างรออนุมัติ");

            requestId = reader.GetInt64(0);
            currentTypeId = reader.GetInt64(1);
            currentTypeName = reader.GetString(2);
            currentKind = reader.GetString(3);
            currentDate = reader.GetFieldValue<DateOnly>(4);
            currentStartTime = reader.GetFieldValue<TimeOnly>(5);
            currentHours = reader.GetDecimal(6);
            requestedTypeId = reader.GetInt64(7);
            requestedTypeName = reader.GetString(8);
            requestedKind = reader.GetString(9);
            requestedDate = reader.GetFieldValue<DateOnly>(10);
            requestedStartTime = reader.GetFieldValue<TimeOnly>(11);
            requestedHours = reader.GetDecimal(12);
            requestReason = reader.GetString(13);
            currentHasMedicalCertificate = reader.IsDBNull(14) ? null : reader.GetBoolean(14);
            requestedHasMedicalCertificate = reader.IsDBNull(15) ? null : reader.GetBoolean(15);
            creatorEmployeeId = reader.GetString(16);
        }

        if (approve)
        {
            var quotaError = await ValidateQuotaAvailability(
                connection, transaction, creatorEmployeeId, requestedTypeId,
                requestedDate.Year, requestedHours, documentId, cancellationToken);
            if (quotaError is not null)
                return Conflict(quotaError);
        }

        const string updateRequestSql = """
            UPDATE public.leave_edit_requests
            SET status = @status,
                reviewed_by = @reviewed_by,
                reviewed_by_name = @reviewed_by_name,
                reviewed_at = CURRENT_TIMESTAMP,
                review_remark = @remark
            WHERE id = @request_id AND status = 'PENDING'
            """;
        await using (var command = new NpgsqlCommand(updateRequestSql, connection, transaction))
        {
            command.Parameters.AddWithValue("status", approve ? "APPROVED" : "REJECTED");
            command.Parameters.AddWithValue("reviewed_by", review.ActionBy);
            command.Parameters.AddWithValue("reviewed_by_name", review.ActionByName);
            command.Parameters.Add(new NpgsqlParameter<string?>("remark", review.Remark));
            command.Parameters.AddWithValue("request_id", requestId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string approveDocumentSql = """
            UPDATE public.leave_documents
            SET leave_type_id = @leave_type_id,
                leave_date = @leave_date,
                start_time = @start_time,
                leave_hours = @hours,
                has_medical_certificate = @has_medical_certificate,
                status = 'APPROVED'
            WHERE id = @document_id AND status = 'EDIT_REQUESTED'
            """;
        const string rejectDocumentSql = """
            UPDATE public.leave_documents
            SET status = 'APPROVED'
            WHERE id = @document_id AND status = 'EDIT_REQUESTED'
            """;

        await using (var command = new NpgsqlCommand(
            approve ? approveDocumentSql : rejectDocumentSql,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("document_id", documentId);
            if (approve)
            {
                command.Parameters.AddWithValue("leave_type_id", requestedTypeId);
                command.Parameters.AddWithValue("leave_date", requestedDate);
                command.Parameters.AddWithValue("start_time", requestedStartTime);
                command.Parameters.AddWithValue("hours", requestedHours);
                command.Parameters.Add(new NpgsqlParameter<bool?>(
                    "has_medical_certificate", requestedHasMedicalCertificate));
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (approve &&
            (currentTypeId != requestedTypeId ||
             currentHasMedicalCertificate != requestedHasMedicalCertificate))
        {
            await ApplyDefaultBonusDeduction(
                connection, transaction, documentId, force: false, cancellationToken);
        }

        var approvedAttachmentCount = 0;
        if (approve)
        {
            const string promoteAttachmentsSql = """
                UPDATE public.leave_document_attachments
                SET leave_edit_request_id = NULL
                WHERE leave_edit_request_id = @request_id
                """;
            await using var command = new NpgsqlCommand(promoteAttachmentsSql, connection, transaction);
            command.Parameters.AddWithValue("request_id", requestId);
            approvedAttachmentCount = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var changedFields = DescribeChangedFields(
            currentTypeId, currentTypeName, currentKind, currentDate, currentStartTime, currentHours,
            requestedTypeId, requestedTypeName, currentKind, requestedDate, requestedStartTime,
            requestedHours);
        if (currentHasMedicalCertificate != requestedHasMedicalCertificate)
        {
            changedFields.Add($"ใบรับรองแพทย์จาก {MedicalCertificateText(currentHasMedicalCertificate)} เป็น {MedicalCertificateText(requestedHasMedicalCertificate)}");
        }
        var changes = changedFields.Count == 0
            ? "ไม่มีฟิลด์ข้อมูลการลาเปลี่ยนแปลง"
            : string.Join(", ", changedFields);
        var details = approve
            ? $"อนุมัติคำขอแก้ไข: {changes}; เหตุผลในการขอแก้ไข: {requestReason}"
            : $"ไม่อนุมัติคำขอแก้ไข: {changes}; เหตุผลในการขอแก้ไข: {requestReason}";
        if (approve && approvedAttachmentCount > 0)
            details += $"; เพิ่มไฟล์แนบ {approvedAttachmentCount} ไฟล์";
        if (!string.IsNullOrWhiteSpace(review.Remark))
            details += $"; หมายเหตุการพิจารณา: {review.Remark.Trim()}";

        await InsertHistory(
            connection,
            transaction,
            documentId,
            approve ? "APPROVE_EDIT_REQUEST" : "REJECT",
            details,
            review.ActionBy,
            review.ActionByName,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        if (approve)
            await SyncCalendarSafely(documentId);
        return NoContent();
    }

    private async Task<IActionResult> ReviewPendingCancelRequest(
        long documentId,
        ReviewLeaveDocumentRequest review,
        bool approve,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(review.ActionBy) ||
            string.IsNullOrWhiteSpace(review.ActionByName) ||
            !await IsAuthenticatedActor(review.ActionBy, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับผู้ดำเนินการ");

        var permissionError = await ValidateReviewer(
            documentId, review.ActionBy,
            approve ? "APPROVE_EDIT" : "REJECT_EDIT",
            cancellationToken, "LEAVE_REVISIONS");
        if (permissionError is not null)
            return permissionError;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string selectSql = """
            SELECT r.id, r.request_reason, d.document_no, d.leave_date,
                   d.start_time, d.status
            FROM public.leave_cancel_requests r
            JOIN public.leave_documents d ON d.id = r.leave_document_id
            WHERE r.leave_document_id = @document_id AND r.status = 'PENDING'
            FOR UPDATE OF r, d
            """;
        long requestId;
        string requestReason;
        string documentNo;
        DateOnly leaveDate;
        TimeOnly startTime;
        string documentStatus;
        await using (var command = new NpgsqlCommand(selectSql, connection, transaction))
        {
            command.Parameters.AddWithValue("document_id", documentId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return Conflict("ไม่พบคำขอยกเลิกที่รอพิจารณา");
            requestId = reader.GetInt64(0);
            requestReason = reader.GetString(1);
            documentNo = reader.GetString(2);
            leaveDate = reader.GetFieldValue<DateOnly>(3);
            startTime = reader.GetFieldValue<TimeOnly>(4);
            documentStatus = reader.GetString(5);
        }

        const string updateRequestSql = """
            UPDATE public.leave_cancel_requests
            SET status = @status, reviewed_by = @reviewed_by,
                reviewed_by_name = @reviewed_by_name,
                reviewed_at = CURRENT_TIMESTAMP, review_remark = @remark
            WHERE id = @request_id AND status = 'PENDING'
            """;
        await using (var command = new NpgsqlCommand(updateRequestSql, connection, transaction))
        {
            command.Parameters.AddWithValue("status", approve ? "APPROVED" : "REJECTED");
            command.Parameters.AddWithValue("reviewed_by", review.ActionBy.Trim());
            command.Parameters.AddWithValue("reviewed_by_name", review.ActionByName.Trim());
            command.Parameters.Add(new NpgsqlParameter<string?>("remark", review.Remark));
            command.Parameters.AddWithValue("request_id", requestId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (approve)
        {
            const string cancelDocumentSql = """
                UPDATE public.leave_documents
                SET status = 'CANCELLED', cancelled_at = CURRENT_TIMESTAMP
                WHERE id = @document_id AND status IN ('PENDING_APPROVAL', 'APPROVED')
                """;
            await using var command = new NpgsqlCommand(cancelDocumentSql, connection, transaction);
            command.Parameters.AddWithValue("document_id", documentId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                return Conflict("เอกสารไม่อยู่ในสถานะที่ยกเลิกได้");
        }

        var details = approve
            ? $"อนุมัติคำขอยกเลิกเอกสาร {documentNo}; เหตุผล: {requestReason}"
            : $"ไม่อนุมัติคำขอยกเลิกเอกสาร {documentNo}; เหตุผล: {requestReason}";
        if (!string.IsNullOrWhiteSpace(review.Remark))
            details += $"; หมายเหตุการพิจารณา: {review.Remark.Trim()}";
        await InsertHistory(connection, transaction, documentId,
            approve ? "APPROVE_CANCEL_REQUEST" : "REJECT_CANCEL_REQUEST",
            details, review.ActionBy, review.ActionByName, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (approve)
        {
            await SyncCalendarSafely(documentId);
            await SendCancellationEmailSafely(
                documentId,
                review.ActionByName,
                requestReason);
        }
        return NoContent();
    }

    private static async Task<LeaveTypeDetails?> FindLeaveType(
        NpgsqlConnection connection,
        long leaveTypeId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT code, name_th FROM public.leave_types WHERE id = @id AND is_active = TRUE";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", leaveTypeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LeaveTypeDetails(reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async Task<LeaveApprover?> ResolveLeaveApprover(
        NpgsqlConnection connection,
        string creatorEmployeeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COALESCE(reporting_approver.employee_code, boss.employee_code),
                   CASE
                       WHEN reporting_approver.employee_code IS NOT NULL THEN reporting_approver.full_name
                       WHEN boss.employee_code IS NOT NULL THEN boss.full_name
                       ELSE COALESCE(NULLIF(BTRIM(company.leave_approver_name), ''),
                                     NULLIF(BTRIM(company.supervisor_name), ''))
                   END
            FROM public.employees creator
            JOIN public.employee_company_info company ON company.employee_id = creator.id
            LEFT JOIN LATERAL
            (
                SELECT employee.employee_code,
                       COALESCE(NULLIF(basic.full_name_th, ''), NULLIF(basic.full_name_en, ''),
                                NULLIF(BTRIM(CONCAT_WS(' ', basic.first_name_th, basic.last_name_th)), ''),
                                NULLIF(BTRIM(CONCAT_WS(' ', basic.first_name_en, basic.last_name_en)), ''),
                                employee.employee_code) AS full_name
                FROM public.employees employee
                JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
                WHERE employee.is_active = TRUE
                  AND (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(company.leave_approver_name, ''))), '\s+', ' ', 'g') IN
                      (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(basic.full_name_th, ''))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(COALESCE(basic.full_name_en, ''))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', basic.first_name_th, basic.last_name_th))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', basic.first_name_en, basic.last_name_en))), '\s+', ' ', 'g'))
                    OR UPPER(BTRIM(COALESCE(company.leave_approver_name, ''))) = UPPER(employee.employee_code))
                ORDER BY employee.id
                LIMIT 1
            ) reporting_approver ON TRUE
            LEFT JOIN LATERAL
            (
                SELECT employee.employee_code,
                       COALESCE(NULLIF(basic.full_name_th, ''), NULLIF(basic.full_name_en, ''),
                                NULLIF(BTRIM(CONCAT_WS(' ', basic.first_name_th, basic.last_name_th)), ''),
                                NULLIF(BTRIM(CONCAT_WS(' ', basic.first_name_en, basic.last_name_en)), ''),
                                employee.employee_code) AS full_name
                FROM public.employees employee
                JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
                WHERE employee.is_active = TRUE
                  AND (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(company.supervisor_name, ''))), '\s+', ' ', 'g') IN
                      (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(basic.full_name_th, ''))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(COALESCE(basic.full_name_en, ''))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', basic.first_name_th, basic.last_name_th))), '\s+', ' ', 'g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', basic.first_name_en, basic.last_name_en))), '\s+', ' ', 'g'))
                    OR UPPER(BTRIM(COALESCE(company.supervisor_name, ''))) = UPPER(employee.employee_code))
                ORDER BY employee.id
                LIMIT 1
            ) boss ON TRUE
            WHERE creator.employee_code = @employee_code AND creator.is_active = TRUE
            LIMIT 1
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("employee_code", creatorEmployeeId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(1)) return null;
        var name = reader.GetString(1).Trim();
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new LeaveApprover(reader.IsDBNull(0) ? null : reader.GetString(0), name);
    }

    private async Task SendApprovalEmailSafely(
        string creatorEmployeeId,
        string creatorName,
        string leaveTypeName,
        IReadOnlyCollection<LeaveApprovalEmailItem> items,
        string leaveReason,
        CancellationToken cancellationToken)
    {
        try
        {
            await approvalEmailService.SendAsync(
                creatorEmployeeId,
                creatorName,
                leaveTypeName,
                items,
                leaveReason.Trim(),
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Leave approval email failed for employee {EmployeeId}, documents {DocumentNumbers}",
                creatorEmployeeId,
                string.Join(", ", items.Select(item => item.DocumentNo)));
        }
    }

    private async Task SyncCalendarSafely(long documentId)
    {
        try
        {
            // Complete the out-of-process sync even if the browser disconnects after
            // the leave transaction was committed. Failures are persisted for retry.
            await outlookCalendarSyncService.SyncAsync(documentId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Outlook calendar sync failed for leave document {DocumentId}",
                documentId);
        }
    }

    private async Task SendDecisionEmailSafely(
        long documentId,
        bool approved,
        string reviewerEmployeeId,
        string reviewerName,
        string? remark)
    {
        try
        {
            // The leave decision has already been committed. Do not let a mail
            // provider failure roll back or change the recorded decision.
            await decisionEmailService.SendAsync(
                documentId,
                approved,
                reviewerEmployeeId,
                reviewerName,
                remark,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Leave decision email failed for document {DocumentId}, decision {Decision}",
                documentId,
                approved ? "APPROVED" : "REJECTED");
        }
    }

    private async Task SendCancellationEmailSafely(
        long documentId,
        string cancelledByName,
        string? cancellationReason)
    {
        try
        {
            // Cancellation is already committed. Mail failures are logged and
            // must not change the document status or restored quota.
            await cancellationEmailService.SendAsync(
                documentId,
                cancelledByName,
                cancellationReason,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Leave cancellation email failed for document {DocumentId}",
                documentId);
        }
    }

    private static async Task<string?> ValidateQuotaAvailability(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string employeeId,
        long leaveTypeId,
        int quotaYear,
        decimal requestedHours,
        long? excludedDocumentId,
        CancellationToken cancellationToken)
    {
        const string quotaSql = """
            SELECT quota_hours
            FROM public.leave_quotas
            WHERE employee_id = @employee_id
              AND leave_type_id = @leave_type_id
              AND quota_year = @quota_year
            FOR UPDATE
            """;

        decimal? quotaHours;
        await using (var command = new NpgsqlCommand(quotaSql, connection, transaction))
        {
            command.Parameters.AddWithValue("employee_id", employeeId.Trim());
            command.Parameters.AddWithValue("leave_type_id", leaveTypeId);
            command.Parameters.AddWithValue("quota_year", quotaYear);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            quotaHours = value is null or DBNull ? null : Convert.ToDecimal(value);
        }

        if (!quotaHours.HasValue)
            return $"โควต้าวันลาไม่พอ เนื่องจากยังไม่ได้กำหนดโควต้าประเภทนี้สำหรับปี {quotaYear}";

        const string usedSql = """
            SELECT COALESCE(SUM(leave_hours), 0)
            FROM public.leave_documents
            WHERE creator_employee_id = @employee_id
              AND leave_type_id = @leave_type_id
              AND EXTRACT(YEAR FROM leave_date)::INT = @quota_year
              AND status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED')
              AND (@excluded_document_id IS NULL OR id <> @excluded_document_id)
            """;

        decimal usedHours;
        await using (var command = new NpgsqlCommand(usedSql, connection, transaction))
        {
            command.Parameters.AddWithValue("employee_id", employeeId.Trim());
            command.Parameters.AddWithValue("leave_type_id", leaveTypeId);
            command.Parameters.AddWithValue("quota_year", quotaYear);
            command.Parameters.Add(new NpgsqlParameter<long?>("excluded_document_id", excludedDocumentId));
            usedHours = Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken));
        }

        var remainingHours = quotaHours.Value - usedHours;
        if (requestedHours > remainingHours)
        {
            return $"โควต้าวันลาไม่พอ คงเหลือ {Math.Max(remainingHours, 0):0.##} ชั่วโมง " +
                   $"แต่ต้องการ {requestedHours:0.##} ชั่วโมง";
        }

        return null;
    }

    private static string? ValidateMedicalCertificate(
        string leaveTypeCode,
        bool? hasMedicalCertificate,
        IReadOnlyList<LeaveAttachmentUploadDto>? attachments)
    {
        if (string.Equals(leaveTypeCode, "SICK", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasMedicalCertificate.HasValue)
                return "กรุณาระบุว่ามีใบรับรองแพทย์หรือไม่";
            if (hasMedicalCertificate == true && (attachments is null || attachments.Count == 0))
                return "กรุณาแนบใบรับรองแพทย์";
        }

        return ValidateAttachments(attachments);
    }

    private static string? ValidateAttachments(IReadOnlyList<LeaveAttachmentUploadDto>? attachments)
    {
        if (attachments is null || attachments.Count == 0) return null;
        if (attachments.Count > 10) return "แนบไฟล์ได้ไม่เกิน 10 ไฟล์ต่อครั้ง";

        const int maxFileSize = 10 * 1024 * 1024;
        foreach (var attachment in attachments)
        {
            var extension = Path.GetExtension(Path.GetFileName(attachment.FileName)).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(attachment.FileName) || attachment.FileName.Length > 255 ||
                extension is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
                return "รองรับเอกสารแนบเฉพาะไฟล์ PDF, JPG และ PNG";
            if (attachment.Content is null || attachment.Content.Length == 0 || attachment.Content.Length > maxFileSize)
                return "เอกสารแนบแต่ละไฟล์ต้องมีขนาดมากกว่า 0 และไม่เกิน 10 MB";
            if (attachment.ContentType?.Length > 150)
                return "ชนิดไฟล์เอกสารแนบไม่ถูกต้อง";
        }
        return null;
    }

    private static async Task InsertAttachment(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long documentId,
        LeaveAttachmentUploadDto attachment,
        string uploadedBy,
        long? editRequestId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.leave_document_attachments
                (leave_document_id, original_file_name, content_type,
                 file_size_bytes, file_content, uploaded_by, leave_edit_request_id)
            VALUES
                (@document_id, @original_file_name, @content_type,
                 @file_size_bytes, @file_content, @uploaded_by, @edit_request_id)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("original_file_name", Path.GetFileName(attachment.FileName));
        command.Parameters.AddWithValue(
            "content_type",
            string.IsNullOrWhiteSpace(attachment.ContentType)
                ? "application/octet-stream"
                : attachment.ContentType);
        command.Parameters.AddWithValue("file_size_bytes", attachment.Content.LongLength);
        command.Parameters.AddWithValue("file_content", NpgsqlDbType.Bytea, attachment.Content);
        command.Parameters.AddWithValue("uploaded_by", uploadedBy);
        command.Parameters.Add(new NpgsqlParameter<long?>("edit_request_id", editRequestId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<LeaveBonusDeductionDto?> FindBonusDeduction(
        long documentId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, bonus_deduction_enabled, bonus_deduction_percent,
                   bonus_deduction_waived, bonus_deduction_reason,
                   bonus_deduction_overridden, bonus_deduction_updated_by,
                   bonus_deduction_updated_by_name, bonus_deduction_updated_at
            FROM public.leave_documents
            WHERE id = @id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new LeaveBonusDeductionDto(
            reader.GetInt64(0), reader.GetBoolean(1), reader.GetDecimal(2),
            reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetBoolean(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));
    }

    private static async Task ApplyDefaultBonusDeduction(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long documentId,
        bool force,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE public.leave_documents document SET
                bonus_deduction_enabled = leave_type.default_bonus_deduction_enabled,
                bonus_deduction_percent = CASE
                    WHEN leave_type.code = 'SICK'
                         AND document.has_medical_certificate IS TRUE THEN 5
                    WHEN leave_type.code = 'SICK' THEN 10
                    ELSE leave_type.default_bonus_deduction_percent
                END,
                bonus_deduction_waived = FALSE,
                bonus_deduction_reason = NULL
            FROM public.leave_types leave_type
            WHERE document.id = @id
              AND leave_type.id = document.leave_type_id
              AND (@force OR document.bonus_deduction_overridden = FALSE)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("force", force);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string YesNo(bool value) => value ? "ใช่" : "ไม่ใช่";

    private static LeaveDocumentDto ReadDocument(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6),
        reader.GetInt64(7), reader.GetString(8), reader.GetString(9), reader.GetString(10),
        reader.GetFieldValue<DateOnly>(11), reader.GetFieldValue<TimeOnly>(12), reader.GetDecimal(13),
        reader.GetString(14), reader.GetString(15), reader.GetFieldValue<DateTimeOffset>(16),
        reader.IsDBNull(17) ? null : reader.GetBoolean(17), null, null);

    private static int CurrentBuddhistYear() => BangkokNow().Year + 543;

    private static string FormatDocumentNumber(int buddhistYear, int groupNumber, int itemNumber) =>
        $"LV-{buddhistYear:0000}-{groupNumber:000000}-{itemNumber:00}";

    private static async Task<int> ReserveNextDocumentGroupNumber(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int buddhistYear,
        CancellationToken cancellationToken)
    {
        // Serialize number allocation for each Buddhist year. This keeps concurrent
        // single-day and multi-day requests from receiving the same group number.
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(1279411286, @document_year)",
            connection,
            transaction))
        {
            lockCommand.Parameters.AddWithValue("document_year", buddhistYear);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string nextNumberSql = """
            SELECT COALESCE(
                       MAX((SUBSTRING(document_no FROM @number_pattern))::INTEGER),
                       0) + 1
            FROM public.leave_documents
            """;
        await using var command = new NpgsqlCommand(nextNumberSql, connection, transaction);
        command.Parameters.AddWithValue(
            "number_pattern",
            $"^LV-{buddhistYear:0000}-([0-9]{{6}})");
        var nextNumber = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (nextNumber > 999999)
            throw new InvalidOperationException($"เลขชุดเอกสารการลาปี {buddhistYear} เต็มแล้ว");
        return nextNumber;
    }

    private static bool IsValid(long leaveTypeId, string leaveKind, decimal hours, string? reason) =>
        leaveTypeId > 0 &&
        leaveKind is "ADVANCE" or "RETROACTIVE" &&
        hours > 0 &&
        hours <= 24 &&
        !string.IsNullOrWhiteSpace(reason);

    private static bool IsRetroactiveBeyondLimit(string leaveKind, DateOnly leaveDate) =>
        string.Equals(leaveKind, "RETROACTIVE", StringComparison.OrdinalIgnoreCase) &&
        leaveDate < DateOnly.FromDateTime(BangkokNow()).AddDays(-3);

    private static string? ValidateLeaveKindDate(string leaveKind, DateOnly leaveDate)
    {
        var today = DateOnly.FromDateTime(BangkokNow());
        if (string.Equals(leaveKind, "ADVANCE", StringComparison.OrdinalIgnoreCase) && leaveDate < today)
            return "วันที่ลาเป็นวันที่ย้อนหลัง กรุณาเลือกชนิดการลาเป็นลาย้อนหลัง";
        if (string.Equals(leaveKind, "RETROACTIVE", StringComparison.OrdinalIgnoreCase) && leaveDate > today)
            return "วันที่ลาเป็นวันที่ในอนาคต กรุณาเลือกชนิดการลาเป็นลาล่วงหน้า";
        return null;
    }

    private static DateTime BangkokNow() => DateTime.UtcNow.AddHours(7);

    private static string MedicalCertificateText(bool? value) => value switch
    {
        true => "มีใบรับรองแพทย์",
        false => "ไม่มีใบรับรองแพทย์",
        _ => "ไม่ระบุ"
    };

    private static List<string> DescribeChangedFields(
        long currentTypeId,
        string currentTypeName,
        string currentKind,
        DateOnly currentDate,
        TimeOnly currentStartTime,
        decimal currentHours,
        long requestedTypeId,
        string requestedTypeName,
        string requestedKind,
        DateOnly requestedDate,
        TimeOnly requestedStartTime,
        decimal requestedHours)
    {
        var changes = new List<string>();
        if (currentKind != requestedKind)
            changes.Add($"ชนิดการลา {LeaveKindText(currentKind)} → {LeaveKindText(requestedKind)}");
        if (currentTypeId != requestedTypeId)
            changes.Add($"ประเภทการลา {currentTypeName} → {requestedTypeName}");
        if (currentDate != requestedDate)
            changes.Add($"วันที่ลา {currentDate:dd/MM/yyyy} → {requestedDate:dd/MM/yyyy}");
        if (currentStartTime != requestedStartTime)
            changes.Add($"เวลาเริ่มต้น {currentStartTime:HH\\:mm} → {requestedStartTime:HH\\:mm}");
        if (currentHours != requestedHours)
            changes.Add($"จำนวนชั่วโมง {currentHours:0.##} → {requestedHours:0.##}");
        return changes;
    }

    private static string LeaveKindText(string kind) =>
        kind == "ADVANCE" ? "ลาล่วงหน้า" : "ลาย้อนหลัง";

    private static async Task InsertHistory(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long documentId,
        string action,
        string details,
        string actionBy,
        string actionByName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.leave_document_history
                (leave_document_id, action, details_text, action_by, action_by_name)
            VALUES (@document_id, @action, @details, @action_by, @action_by_name)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("details", details);
        command.Parameters.AddWithValue("action_by", actionBy);
        command.Parameters.AddWithValue("action_by_name", actionByName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record LeaveTypeDetails(string Code, string Name);
    private sealed record LeaveApprover(string? EmployeeId, string Name);
}
