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
    IWebHostEnvironment environment) : ControllerBase
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
            var canViewAll = await actionPermissionService.HasPermission(
                    actingEmployeeId, "LEAVE_PENDING", "VIEW_ALL", cancellationToken) ||
                await actionPermissionService.HasPermission(
                    actingEmployeeId, "LEAVE_PENDING", "APPROVE", cancellationToken) ||
                await actionPermissionService.HasPermission(
                    actingEmployeeId, "LEAVE_PENDING", "REJECT", cancellationToken);
            restrictPendingToApprover = !canViewAll;
        }

        const string sql = """
            SELECT d.id, d.document_no, d.creator_employee_id, d.creator_name,
                   d.creator_department,
                   COALESCE(reporting_approver.employee_code, d.approver_employee_id),
                   COALESCE(reporting_approver.full_name, creator_company.leave_approver_name, d.approver_name),
                   t.id, t.code, t.name_th, d.leave_kind, d.leave_date, d.start_time,
                   d.leave_hours, d.leave_reason, d.status, d.created_at,
                   COALESCE(@acting_employee_id IN
                       (reporting_approver.employee_code, upper_reporting_approver.employee_code), FALSE)
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
                  (reporting_approver.employee_code, upper_reporting_approver.employee_code))
            ORDER BY d.created_at DESC
            """;

        var result = new List<LeaveDocumentDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<string?>("creator_employee_id", creatorEmployeeId));
        command.Parameters.Add(new NpgsqlParameter<string?>("approver_employee_id", approverEmployeeId));
        command.Parameters.Add(new NpgsqlParameter<string?>("acting_employee_id", actingEmployeeId));
        command.Parameters.Add(new NpgsqlParameter<string?>("status", status));
        command.Parameters.AddWithValue("restrict_pending", restrictPendingToApprover);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadDocument(reader) with { CanCurrentUserReview = reader.GetBoolean(17) });

        return Ok(result);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<LeaveDocumentDto>> GetById(long id, CancellationToken cancellationToken)
    {
        var document = await FindDocument(id, cancellationToken);
        if (document is null)
            return NotFound();

        var pendingRequest = await FindPendingEditRequest(id, cancellationToken);
        return Ok(document with { PendingEditRequest = pendingRequest });
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
        if (IsRetroactiveBeyondLimit(request.LeaveKind, request.LeaveDate))
            return BadRequest("ลาย้อนหลังได้ไม่เกิน 3 วันปฏิทินนับจากวันที่สร้างเอกสาร");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var leaveTypeCode = await FindLeaveTypeCode(connection, request.LeaveTypeId, cancellationToken);
        if (leaveTypeCode is null)
            return BadRequest("ไม่พบประเภทการลา");
        var certificateError = ValidateMedicalCertificate(
            leaveTypeCode, request.HasMedicalCertificate, request.Attachment);
        if (certificateError is not null)
            return BadRequest(certificateError);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

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
            command.Parameters.Add(new NpgsqlParameter<string?>("approver_id", request.ApproverEmployeeId));
            command.Parameters.AddWithValue("approver_name", request.ApproverName);
            command.Parameters.AddWithValue("leave_type_id", request.LeaveTypeId);
            command.Parameters.AddWithValue("leave_kind", request.LeaveKind);
            command.Parameters.AddWithValue("leave_date", request.LeaveDate);
            command.Parameters.AddWithValue("start_time", request.StartTime);
            command.Parameters.AddWithValue("hours", request.LeaveHours);
            command.Parameters.AddWithValue("reason", request.LeaveReason.Trim());
            command.Parameters.Add(new NpgsqlParameter<bool?>(
                "has_medical_certificate",
                leaveTypeCode == "SICK" ? request.HasMedicalCertificate : null));
            id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        var documentNo = $"LV-{DateTime.Today:yyyy}-{id:000000}";
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
        if (request.Attachment is not null)
        {
            var storedAttachment = await StoreAttachment(request.Attachment, cancellationToken);
            await InsertAttachment(
                connection, transaction, id, storedAttachment,
                request.CreatorEmployeeId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

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
        if (request.Items.Count > 366)
            return BadRequest("จำนวนรายการวันที่ลาต้องไม่เกิน 366 รายการ");
        if (request.Items.Any(item => item.LeaveHours <= 0 || item.LeaveHours > 24))
            return BadRequest("จำนวนชั่วโมงของแต่ละรายการต้องมากกว่า 0 และไม่เกิน 24 ชั่วโมง");
        if (request.Items.Select(item => item.LeaveDate).Distinct().Count() != request.Items.Count)
            return BadRequest("วันที่ลาในรายการต้องไม่ซ้ำกัน");
        if (request.Items.Any(item => IsRetroactiveBeyondLimit(request.LeaveKind, item.LeaveDate)))
            return BadRequest("ลาย้อนหลังได้ไม่เกิน 3 วันปฏิทินนับจากวันที่สร้างเอกสาร กรุณาตรวจสอบวันที่ในรายการ");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var leaveTypeCode = await FindLeaveTypeCode(connection, request.LeaveTypeId, cancellationToken);
        if (leaveTypeCode is null)
            return BadRequest("ไม่พบประเภทการลา");
        var certificateError = ValidateMedicalCertificate(
            leaveTypeCode, request.HasMedicalCertificate, request.Attachment);
        if (certificateError is not null)
            return BadRequest(certificateError);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var createdIds = new List<long>(request.Items.Count);

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

        foreach (var item in request.Items.OrderBy(item => item.LeaveDate))
        {
            var leaveDate = item.LeaveDate;
            long id;
            await using (var command = new NpgsqlCommand(insertSql, connection, transaction))
            {
                command.Parameters.AddWithValue("temporary_no", $"TMP-{Guid.NewGuid():N}"[..30]);
                command.Parameters.AddWithValue("creator_id", request.CreatorEmployeeId);
                command.Parameters.AddWithValue("creator_name", request.CreatorName);
                command.Parameters.AddWithValue("department", request.CreatorDepartment);
                command.Parameters.Add(new NpgsqlParameter<string?>("approver_id", request.ApproverEmployeeId));
                command.Parameters.AddWithValue("approver_name", request.ApproverName);
                command.Parameters.AddWithValue("leave_type_id", request.LeaveTypeId);
                command.Parameters.AddWithValue("leave_kind", request.LeaveKind);
                command.Parameters.AddWithValue("leave_date", leaveDate);
                command.Parameters.AddWithValue("start_time", item.StartTime);
                command.Parameters.AddWithValue("hours", item.LeaveHours);
                command.Parameters.AddWithValue("reason", request.LeaveReason.Trim());
                command.Parameters.Add(new NpgsqlParameter<bool?>(
                    "has_medical_certificate",
                    leaveTypeCode == "SICK" ? request.HasMedicalCertificate : null));
                id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            }

            var documentNo = $"LV-{leaveDate.Year}-{id:000000}";
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

        if (request.Attachment is not null)
        {
            var storedAttachment = await StoreAttachment(request.Attachment, cancellationToken);
            foreach (var documentId in createdIds)
            {
                await InsertAttachment(
                    connection, transaction, documentId, storedAttachment,
                    request.CreatorEmployeeId, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);

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

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string currentSql = """
            SELECT d.leave_type_id, current_type.name_th, d.leave_kind, d.leave_date,
                   d.start_time, d.leave_hours, d.leave_reason, requested_type.name_th
            FROM public.leave_documents d
            JOIN public.leave_types current_type ON current_type.id = d.leave_type_id
            JOIN public.leave_types requested_type ON requested_type.id = @requested_type_id
            WHERE d.id = @id AND d.status = 'PENDING_APPROVAL'
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
        }

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
        return NoContent();
    }

    [HttpPost("{id:long}/cancel")]
    public async Task<IActionResult> Cancel(
        long id,
        ReviewLeaveDocumentRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActionBy) ||
            string.IsNullOrWhiteSpace(request.ActionByName))
        {
            return BadRequest("ไม่พบข้อมูลผู้ยกเลิกเอกสาร");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string sql = """
            UPDATE public.leave_documents
            SET status = 'CANCELLED',
                cancelled_at = CURRENT_TIMESTAMP
            WHERE id = @id
              AND
              (
                  status = 'PENDING_APPROVAL'
                  OR
                  (
                      status = 'APPROVED'
                      AND (leave_date + start_time) >
                          (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Bangkok')
                  )
              )
            RETURNING document_no
            """;

        string? documentNo;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            documentNo = (string?)await command.ExecuteScalarAsync(cancellationToken);
        }

        if (documentNo is null)
        {
            return Conflict(
                "เอกสารรออนุมัติยกเลิกได้เสมอ ส่วนเอกสารที่อนุมัติแล้ว " +
                "ต้องยกเลิกก่อนถึงวันเวลาเริ่มลา");
        }

        var details = string.IsNullOrWhiteSpace(request.Remark)
            ? $"ยกเลิกเอกสาร {documentNo}"
            : $"ยกเลิกเอกสาร {documentNo}; เหตุผล: {request.Remark.Trim()}";
        await InsertHistory(
            connection,
            transaction,
            id,
            "CANCEL",
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

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string lockSql = """
            SELECT d.status, d.leave_type_id, current_type.name_th, d.leave_kind,
                   d.leave_date, d.start_time, d.leave_hours,
                   requested_type.name_th
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
        }

        if (documentStatus is not ("APPROVED" or "EDIT_REQUESTED"))
            return Conflict("ขอแก้ไขได้เฉพาะเอกสารที่อนุมัติแล้วหรือมีคำขอแก้ไขอยู่");

        const string upsertSql = """
            INSERT INTO public.leave_edit_requests
                (leave_document_id, requested_leave_type_id, requested_leave_kind,
                 requested_leave_date, requested_start_time, requested_leave_hours, request_reason,
                 requested_by, requested_by_name)
            VALUES
                (@document_id, @leave_type_id, @leave_kind, @leave_date, @start_time, @hours,
                 @reason, @requested_by, @requested_by_name)
            ON CONFLICT (leave_document_id) WHERE status = 'PENDING'
            DO UPDATE SET
                requested_leave_type_id = EXCLUDED.requested_leave_type_id,
                requested_leave_kind = EXCLUDED.requested_leave_kind,
                requested_leave_date = EXCLUDED.requested_leave_date,
                requested_start_time = EXCLUDED.requested_start_time,
                requested_leave_hours = EXCLUDED.requested_leave_hours,
                request_reason = EXCLUDED.request_reason,
                requested_by = EXCLUDED.requested_by,
                requested_by_name = EXCLUDED.requested_by_name
            """;
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
            command.Parameters.AddWithValue("reason", request.RequestReason.Trim());
            command.Parameters.AddWithValue("requested_by", request.RequestedBy);
            command.Parameters.AddWithValue("requested_by_name", request.RequestedByName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new NpgsqlCommand("UPDATE public.leave_documents SET status = 'EDIT_REQUESTED' WHERE id = @id", connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var action = documentStatus == "APPROVED" ? "REQUEST_EDIT" : "EDIT";
        var changedFields = DescribeChangedFields(
            currentTypeId, currentTypeName, currentKind, currentDate, currentStartTime, currentHours,
            request.LeaveTypeId, requestedTypeName, request.LeaveKind, request.LeaveDate, request.StartTime,
            request.LeaveHours);
        var changedDetails = changedFields.Count == 0
            ? "ไม่มีฟิลด์ข้อมูลการลาเปลี่ยนแปลง"
            : string.Join(", ", changedFields);
        var details = $"ขอแก้ไข: {changedDetails}; เหตุผล: {request.RequestReason.Trim()}";
        await InsertHistory(connection, transaction, id, action, details, request.RequestedBy, request.RequestedByName, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

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
            UPDATE public.leave_edit_requests
            SET status = 'CANCELLED', reviewed_at = CURRENT_TIMESTAMP
            WHERE leave_document_id = @document_id AND status = 'PENDING'
            """;
        await using (var command = new NpgsqlCommand(cancelSql, connection, transaction))
        {
            command.Parameters.AddWithValue("document_id", id);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                return NotFound("ไม่พบคำขอแก้ไขที่รอดำเนินการ");
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
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT reporting_approver.employee_code,
                   upper_reporting_approver.employee_code
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
        if (string.Equals(reportingApprover, actorEmployeeId.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(upperReportingApprover, actorEmployeeId.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;
        if (await actionPermissionService.HasPermission(
                actorEmployeeId, "LEAVE_PENDING", actionKey, cancellationToken))
            return null;
        return StatusCode(
            StatusCodes.Status403Forbidden,
            $"ไม่มีสิทธิ์{(actionKey == "APPROVE" ? "อนุมัติ" : "ไม่อนุมัติ")}เอกสารนี้");
    }

    private async Task<bool> IsAuthenticatedActor(
        string actorEmployeeId,
        CancellationToken cancellationToken)
    {
        var authenticatedEmployeeId = await ResolveAuthenticatedEmployeeId(cancellationToken);
        return !string.IsNullOrWhiteSpace(authenticatedEmployeeId) &&
            string.Equals(authenticatedEmployeeId, actorEmployeeId.Trim(), StringComparison.OrdinalIgnoreCase);
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
                   COALESCE(reporting_approver.full_name, creator_company.leave_approver_name, d.approver_name),
                   t.id, t.code, t.name_th, d.leave_kind, d.leave_date, d.start_time,
                   d.leave_hours, d.leave_reason, d.status, d.created_at
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
            reader.GetFieldValue<TimeOnly>(6), reader.GetDecimal(7), reader.GetString(8),
            reader.GetString(9), reader.GetString(10), reader.GetString(11),
            reader.GetFieldValue<DateTimeOffset>(12));
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

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string selectSql = """
            SELECT r.id,
                   d.leave_type_id, current_type.name_th,
                   d.leave_kind, d.leave_date, d.start_time, d.leave_hours,
                   r.requested_leave_type_id, requested_type.name_th,
                   r.requested_leave_kind, r.requested_leave_date, r.requested_start_time,
                   r.requested_leave_hours, r.request_reason
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
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var changedFields = DescribeChangedFields(
            currentTypeId, currentTypeName, currentKind, currentDate, currentStartTime, currentHours,
            requestedTypeId, requestedTypeName, currentKind, requestedDate, requestedStartTime,
            requestedHours);
        var changes = changedFields.Count == 0
            ? "ไม่มีฟิลด์ข้อมูลการลาเปลี่ยนแปลง"
            : string.Join(", ", changedFields);
        var details = approve
            ? $"อนุมัติคำขอแก้ไข: {changes}; เหตุผลในการขอแก้ไข: {requestReason}"
            : $"ไม่อนุมัติคำขอแก้ไข: {changes}; เหตุผลในการขอแก้ไข: {requestReason}";
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
        return NoContent();
    }

    private static async Task<string?> FindLeaveTypeCode(
        NpgsqlConnection connection,
        long leaveTypeId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT code FROM public.leave_types WHERE id = @id AND is_active = TRUE";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", leaveTypeId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static string? ValidateMedicalCertificate(
        string leaveTypeCode,
        bool? hasMedicalCertificate,
        LeaveAttachmentUploadDto? attachment)
    {
        if (string.Equals(leaveTypeCode, "SICK", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasMedicalCertificate.HasValue)
                return "กรุณาระบุว่ามีใบรับรองแพทย์หรือไม่";
            if (hasMedicalCertificate == true && attachment is null)
                return "กรุณาแนบใบรับรองแพทย์";
        }

        if (attachment is null)
            return null;

        const int maxFileSize = 10 * 1024 * 1024;
        var extension = Path.GetExtension(Path.GetFileName(attachment.FileName)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(attachment.FileName) || attachment.FileName.Length > 255 ||
            extension is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
            return "รองรับเอกสารแนบเฉพาะไฟล์ PDF, JPG และ PNG";
        if (attachment.Content is null || attachment.Content.Length == 0 || attachment.Content.Length > maxFileSize)
            return "เอกสารแนบต้องมีขนาดมากกว่า 0 และไม่เกิน 10 MB";
        if (attachment.ContentType?.Length > 150)
            return "ชนิดไฟล์เอกสารแนบไม่ถูกต้อง";
        return null;
    }

    private async Task<StoredAttachment> StoreAttachment(
        LeaveAttachmentUploadDto attachment,
        CancellationToken cancellationToken)
    {
        var originalFileName = Path.GetFileName(attachment.FileName);
        var storedFileName = $"{Guid.NewGuid():N}{Path.GetExtension(originalFileName).ToLowerInvariant()}";
        var relativeDirectory = Path.Combine("App_Data", "leave-attachments");
        var storageDirectory = Path.Combine(environment.ContentRootPath, relativeDirectory);
        Directory.CreateDirectory(storageDirectory);
        var absolutePath = Path.Combine(storageDirectory, storedFileName);
        await System.IO.File.WriteAllBytesAsync(absolutePath, attachment.Content, cancellationToken);
        var relativePath = Path.Combine(relativeDirectory, storedFileName).Replace('\\', '/');
        return new StoredAttachment(
            originalFileName,
            storedFileName,
            relativePath,
            string.IsNullOrWhiteSpace(attachment.ContentType)
                ? "application/octet-stream"
                : attachment.ContentType,
            attachment.Content.LongLength);
    }

    private static async Task InsertAttachment(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long documentId,
        StoredAttachment attachment,
        string uploadedBy,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.leave_document_attachments
                (leave_document_id, original_file_name, stored_file_name,
                 storage_path, content_type, file_size_bytes, uploaded_by)
            VALUES
                (@document_id, @original_file_name, @stored_file_name,
                 @storage_path, @content_type, @file_size_bytes, @uploaded_by)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("original_file_name", attachment.OriginalFileName);
        command.Parameters.AddWithValue("stored_file_name", attachment.StoredFileName);
        command.Parameters.AddWithValue("storage_path", attachment.StoragePath);
        command.Parameters.AddWithValue("content_type", attachment.ContentType);
        command.Parameters.AddWithValue("file_size_bytes", attachment.FileSizeBytes);
        command.Parameters.AddWithValue("uploaded_by", uploadedBy);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record StoredAttachment(
        string OriginalFileName,
        string StoredFileName,
        string StoragePath,
        string ContentType,
        long FileSizeBytes);

    private static LeaveDocumentDto ReadDocument(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6),
        reader.GetInt64(7), reader.GetString(8), reader.GetString(9), reader.GetString(10),
        reader.GetFieldValue<DateOnly>(11), reader.GetFieldValue<TimeOnly>(12), reader.GetDecimal(13),
        reader.GetString(14), reader.GetString(15), reader.GetFieldValue<DateTimeOffset>(16), null);

    private static bool IsValid(long leaveTypeId, string leaveKind, decimal hours, string? reason) =>
        leaveTypeId > 0 &&
        leaveKind is "ADVANCE" or "RETROACTIVE" &&
        hours > 0 &&
        hours <= 24 &&
        !string.IsNullOrWhiteSpace(reason);

    private static bool IsRetroactiveBeyondLimit(string leaveKind, DateOnly leaveDate) =>
        string.Equals(leaveKind, "RETROACTIVE", StringComparison.OrdinalIgnoreCase) &&
        leaveDate < DateOnly.FromDateTime(DateTime.Today).AddDays(-3);

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
}
