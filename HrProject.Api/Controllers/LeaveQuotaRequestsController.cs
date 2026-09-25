using System.Security.Claims;
using HrProject.Shared.Models;
using HrProject.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/leave-quota-requests")]
[Authorize(Policy = "HrApiScope")]
public sealed class LeaveQuotaRequestsController(
    NpgsqlDataSource dataSource,
    PageAccessService pageAccessService,
    PageActionPermissionService actionPermissionService,
    WorkflowEmailNotificationService workflowNotificationService,
    ILogger<LeaveQuotaRequestsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LeaveQuotaRequestDto>>> GetAll(
        [FromQuery] string? employeeId,
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT r.id, r.request_no, r.employee_id, r.leave_type_id,
                   t.name_th, r.quota_year, r.requested_hours,
                   r.approved_hours, r.request_reason, r.status, r.requested_by_name,
                   r.requested_at, r.requested_by,
                   (SELECT COUNT(*)::int FROM public.leave_quota_request_comments comment
                    WHERE comment.leave_quota_request_id=r.id AND comment.is_active=TRUE)
            FROM public.leave_quota_requests r
            JOIN public.leave_types t ON t.id = r.leave_type_id
            WHERE (@employee_id IS NULL OR r.employee_id = @employee_id)
              AND (@status IS NULL OR r.status = @status)
            ORDER BY r.requested_at DESC
            """;

        var result = new List<LeaveQuotaRequestDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<string?>("employee_id", employeeId));
        command.Parameters.Add(new NpgsqlParameter<string?>("status", status));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LeaveQuotaRequestDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetInt16(5),
                reader.GetDecimal(6),
                reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetFieldValue<DateTimeOffset>(11))
                { RequestedBy = reader.GetString(12), CommentCount = reader.GetInt32(13) });
        }

        return Ok(result);
    }

    [HttpGet("{id:long}/comments")]
    public async Task<ActionResult<IReadOnlyList<LeaveQuotaRequestCommentDto>>> GetComments(
        long id, CancellationToken cancellationToken)
    {
        var accessError = await ValidateCommentReadAccess(id, cancellationToken);
        if (accessError is not null) return accessError;

        const string sql = """
            SELECT id, leave_quota_request_id, comment_text, commented_by,
                   commented_by_name, commented_at
            FROM public.leave_quota_request_comments
            WHERE leave_quota_request_id = @id AND is_active = TRUE
            ORDER BY commented_at, id
            """;
        var result = new List<LeaveQuotaRequestCommentDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new LeaveQuotaRequestCommentDto(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
        await reader.DisposeAsync();
        for (var index = 0; index < result.Count; index++)
            result[index] = result[index] with { Attachments = await LoadCommentAttachments(result[index].Id,cancellationToken) };
        return Ok(result);
    }

    [HttpPost("{id:long}/comments")]
    public async Task<ActionResult<LeaveQuotaRequestCommentDto>> AddComment(
        long id, [FromBody] AddLeaveQuotaRequestCommentRequest request,
        CancellationToken cancellationToken)
    {
        var accessError = await ValidateCommentWriteAccess(id, cancellationToken);
        if (accessError is not null) return accessError;

        var actorEmployeeId = await GetActorEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actorEmployeeId)) return Forbid();
        var commentText = request.CommentText?.Trim();
        if (string.IsNullOrWhiteSpace(commentText))
            return BadRequest("กรุณากรอกความคิดเห็น");
        if (commentText.Length > 4000)
            return BadRequest("ความคิดเห็นต้องไม่เกิน 4,000 ตัวอักษร");
        var attachmentError=ValidateCommentAttachments(request.Attachments);
        if(attachmentError is not null)return BadRequest(attachmentError);
        var actorName = await GetEmployeeName(actorEmployeeId, cancellationToken);

        const string sql = """
            INSERT INTO public.leave_quota_request_comments
                (leave_quota_request_id, comment_text, commented_by, commented_by_name)
            VALUES (@request_id, @comment_text, @commented_by, @commented_by_name)
            RETURNING id, commented_at
            """;
        long commentId;
        DateTimeOffset commentedAt;
        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("request_id", id);
            command.Parameters.AddWithValue("comment_text", commentText);
            command.Parameters.AddWithValue("commented_by", actorEmployeeId);
            command.Parameters.AddWithValue("commented_by_name", actorName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            commentId = reader.GetInt64(0);
            commentedAt = reader.GetFieldValue<DateTimeOffset>(1);
        }

        foreach(var attachment in request.Attachments??[])
        {
            await using var attachmentCommand=dataSource.CreateCommand("""
                INSERT INTO public.leave_quota_request_comment_attachments
                    (leave_quota_request_comment_id,original_file_name,content_type,file_size_bytes,file_content)
                VALUES (@comment_id,@name,@type,@size,@content)
                """);
            attachmentCommand.Parameters.AddWithValue("comment_id",commentId);
            attachmentCommand.Parameters.AddWithValue("name",Path.GetFileName(attachment.FileName));
            attachmentCommand.Parameters.AddWithValue("type",attachment.ContentType.ToLowerInvariant());
            attachmentCommand.Parameters.AddWithValue("size",(long)attachment.Content.Length);
            attachmentCommand.Parameters.Add("content",NpgsqlDbType.Bytea).Value=attachment.Content;
            await attachmentCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return Ok(new LeaveQuotaRequestCommentDto(commentId, id, commentText,
            actorEmployeeId, actorName, commentedAt)
        { Attachments=await LoadCommentAttachments(commentId,cancellationToken) });
    }

    [HttpGet("{id:long}/comments/{commentId:long}/attachments/{attachmentId:long}/preview")]
    public async Task<IActionResult> PreviewCommentAttachment(long id,long commentId,long attachmentId,CancellationToken cancellationToken)
    {
        var accessError=await ValidateCommentReadAccess(id,cancellationToken);
        if(accessError is not null)return accessError;
        await using var command=dataSource.CreateCommand("""
            SELECT attachment.content_type,attachment.file_content
            FROM public.leave_quota_request_comment_attachments attachment
            JOIN public.leave_quota_request_comments comment ON comment.id=attachment.leave_quota_request_comment_id
            WHERE comment.leave_quota_request_id=@id AND comment.id=@comment_id AND attachment.id=@attachment_id
            """);
        command.Parameters.AddWithValue("id",id);command.Parameters.AddWithValue("comment_id",commentId);command.Parameters.AddWithValue("attachment_id",attachmentId);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)?File((byte[])reader[1],reader.GetString(0)):NotFound();
    }

    [HttpPost]
    public async Task<ActionResult<LeaveQuotaRequestDto>> Create(
        CreateLeaveQuotaRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.EmployeeId) ||
            request.LeaveTypeId <= 0 ||
            request.QuotaYear is < 2000 or > 2200 ||
            request.RequestedHours <= 0 ||
            string.IsNullOrWhiteSpace(request.RequestReason) ||
            string.IsNullOrWhiteSpace(request.RequestedBy) ||
            string.IsNullOrWhiteSpace(request.RequestedByName))
        {
            return BadRequest("กรุณากรอกข้อมูลคำขอเพิ่มโควต้าให้ครบถ้วน");
        }

        var actorEmployeeId = await GetActorEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actorEmployeeId))
            return Forbid();
        if (!string.Equals(actorEmployeeId, request.RequestedBy, StringComparison.OrdinalIgnoreCase))
            return Forbid();
        if (!await AreDirectReports(actorEmployeeId, [request.EmployeeId.Trim()], cancellationToken))
            return BadRequest("เลือกขอวันลาเพิ่มได้เฉพาะพนักงานที่มี Boss หรือ Reporting To (Leave Approve) เป็นคุณ");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string insertSql = """
            INSERT INTO public.leave_quota_requests
                (request_no, employee_id, leave_type_id, quota_year,
                 requested_hours, request_reason, requested_by,
                 requested_by_name)
            VALUES
                (@temporary_no, @employee_id, @leave_type_id, @quota_year,
                 @requested_hours, @reason, @requested_by,
                 @requested_by_name)
            RETURNING id
            """;

        long id;
        try
        {
            await using var command = new NpgsqlCommand(insertSql, connection, transaction);
            command.Parameters.AddWithValue("temporary_no", $"TMP-{Guid.NewGuid():N}"[..30]);
            command.Parameters.AddWithValue("employee_id", request.EmployeeId.Trim());
            command.Parameters.AddWithValue("leave_type_id", request.LeaveTypeId);
            command.Parameters.AddWithValue("quota_year", request.QuotaYear);
            command.Parameters.AddWithValue("requested_hours", request.RequestedHours);
            command.Parameters.AddWithValue("reason", request.RequestReason.Trim());
            command.Parameters.AddWithValue("requested_by", request.RequestedBy);
            command.Parameters.AddWithValue("requested_by_name", request.RequestedByName);
            id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Conflict("มีคำขอเพิ่มโควต้าประเภทนี้ที่รออนุมัติอยู่แล้ว");
        }

        var requestNo = $"LQR-{request.QuotaYear}-{id:000000}";
        await using (var command = new NpgsqlCommand(
            "UPDATE public.leave_quota_requests SET request_no = @request_no WHERE id = @id",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("request_no", requestNo);
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string historySql = """
            INSERT INTO public.leave_quota_request_history
                (leave_quota_request_id, action, details_text,
                 action_by, action_by_name)
            VALUES
                (@request_id, 'CREATE_REQUEST', @details,
                 @action_by, @action_by_name)
            """;
        await using (var command = new NpgsqlCommand(historySql, connection, transaction))
        {
            command.Parameters.AddWithValue("request_id", id);
            command.Parameters.AddWithValue(
                "details",
                $"ขอเพิ่มโควต้า {request.RequestedHours:0.##} ชั่วโมง ({request.RequestedHours / 8:0.##} วัน); เหตุผล: {request.RequestReason.Trim()}");
            command.Parameters.AddWithValue("action_by", request.RequestedBy);
            command.Parameters.AddWithValue("action_by_name", request.RequestedByName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        await SendNotificationSafely(
            request.RequestedBy,
            "มีคำขอเพิ่มโควต้าวันลารอตรวจสอบ",
            $"เลขที่คำขอ {requestNo}\nผู้ขอ: {request.RequestedByName}\nจำนวน: {request.RequestedHours:0.##} ชั่วโมง\nเหตุผล: {request.RequestReason.Trim()}");
        var created = await FindById(id, cancellationToken);
        return CreatedAtAction(nameof(GetAll), created);
    }

    [HttpPost("batch")]
    public async Task<ActionResult<IReadOnlyList<LeaveQuotaRequestDto>>> CreateBatch(
        CreateMultiEmployeeLeaveQuotaRequest request,
        CancellationToken cancellationToken)
    {
        var employeeRequests = request.Employees?
            .Where(item => !string.IsNullOrWhiteSpace(item.EmployeeId))
            .Select(item => new LeaveQuotaRequestEmployeeItem(
                item.EmployeeId.Trim(),
                item.LeaveTypeId,
                item.RequestedHours,
                item.RequestReason?.Trim() ?? string.Empty))
            .GroupBy(item => item.EmployeeId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList() ?? [];

        if (employeeRequests.Count == 0 ||
            employeeRequests.Count > 200 ||
            employeeRequests.Any(item =>
                item.LeaveTypeId <= 0 ||
                item.RequestedHours <= 0 ||
                string.IsNullOrWhiteSpace(item.RequestReason)) ||
            request.QuotaYear is < 2000 or > 2200 ||
            string.IsNullOrWhiteSpace(request.RequestedBy) ||
            string.IsNullOrWhiteSpace(request.RequestedByName))
        {
            return BadRequest("กรุณาเลือกพนักงานและกรอกข้อมูลคำขอให้ครบถ้วน");
        }

        var actorEmployeeId = await GetActorEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actorEmployeeId))
            return Forbid();
        if (!string.Equals(actorEmployeeId, request.RequestedBy, StringComparison.OrdinalIgnoreCase))
            return Forbid();
        if (!await AreDirectReports(
                actorEmployeeId,
                employeeRequests.Select(item => item.EmployeeId).ToArray(),
                cancellationToken))
        {
            return BadRequest("เลือกขอวันลาเพิ่มได้เฉพาะพนักงานที่มี Boss หรือ Reporting To (Leave Approve) เป็นคุณ");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var createdIds = new List<long>(employeeRequests.Count);

        const string insertSql = """
            INSERT INTO public.leave_quota_requests
                (request_no, employee_id, leave_type_id, quota_year,
                 requested_hours, request_reason, requested_by,
                 requested_by_name)
            VALUES
                (@temporary_no, @employee_id, @leave_type_id, @quota_year,
                 @requested_hours, @reason, @requested_by,
                 @requested_by_name)
            RETURNING id
            """;
        const string historySql = """
            INSERT INTO public.leave_quota_request_history
                (leave_quota_request_id, action, details_text,
                 action_by, action_by_name)
            VALUES
                (@request_id, 'CREATE_REQUEST', @details,
                 @action_by, @action_by_name)
            """;

        try
        {
            foreach (var employeeRequest in employeeRequests)
            {
                long id;
                await using (var command = new NpgsqlCommand(insertSql, connection, transaction))
                {
                    command.Parameters.AddWithValue("temporary_no", $"TMP-{Guid.NewGuid():N}"[..30]);
                    command.Parameters.AddWithValue("employee_id", employeeRequest.EmployeeId);
                    command.Parameters.AddWithValue("leave_type_id", employeeRequest.LeaveTypeId);
                    command.Parameters.AddWithValue("quota_year", request.QuotaYear);
                    command.Parameters.AddWithValue("requested_hours", employeeRequest.RequestedHours);
                    command.Parameters.AddWithValue("reason", employeeRequest.RequestReason);
                    command.Parameters.AddWithValue("requested_by", request.RequestedBy);
                    command.Parameters.AddWithValue("requested_by_name", request.RequestedByName);
                    id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
                }

                var requestNo = $"LQR-{request.QuotaYear}-{id:000000}";
                await using (var command = new NpgsqlCommand(
                    "UPDATE public.leave_quota_requests SET request_no = @request_no WHERE id = @id",
                    connection,
                    transaction))
                {
                    command.Parameters.AddWithValue("request_no", requestNo);
                    command.Parameters.AddWithValue("id", id);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var command = new NpgsqlCommand(historySql, connection, transaction))
                {
                    command.Parameters.AddWithValue("request_id", id);
                    command.Parameters.AddWithValue(
                        "details",
                        $"ขอเพิ่มโควต้า {employeeRequest.RequestedHours:0.##} ชั่วโมง ({employeeRequest.RequestedHours / 8:0.##} วัน); เหตุผล: {employeeRequest.RequestReason}");
                    command.Parameters.AddWithValue("action_by", request.RequestedBy);
                    command.Parameters.AddWithValue("action_by_name", request.RequestedByName);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
                createdIds.Add(id);
            }
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Conflict(
                "พนักงานอย่างน้อยหนึ่งคนมีคำขอประเภทนี้ที่รออนุมัติอยู่แล้ว " +
                "จึงยังไม่สร้างคำขอทั้งชุด");
        }

        await transaction.CommitAsync(cancellationToken);
        await SendNotificationSafely(
            request.RequestedBy,
            "มีคำขอเพิ่มโควต้าวันลาแบบหลายรายการรอตรวจสอบ",
            $"ผู้ขอ: {request.RequestedByName}\nปีโควต้า: {request.QuotaYear}\nจำนวนรายการ: {createdIds.Count}");
        var created = new List<LeaveQuotaRequestDto>(createdIds.Count);
        foreach (var id in createdIds)
        {
            var item = await FindById(id, cancellationToken);
            if (item is not null)
                created.Add(item);
        }

        return StatusCode(StatusCodes.Status201Created, created);
    }

    private async Task SendNotificationSafely(string senderEmployeeId, string title, string details)
    {
        try
        {
            await workflowNotificationService.SendAsync(
                "LEAVE_REQUEST_QUOTA", senderEmployeeId, title, details,
                "/leave/request-quota", CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Leave quota request was saved but email notification failed");
        }
    }

    [HttpPost("{id:long}/approve")]
    public Task<IActionResult> Approve(
        long id,
        ReviewLeaveQuotaRequest request,
        CancellationToken cancellationToken) =>
        Review(id, true, request, cancellationToken);

    [HttpPost("{id:long}/reject")]
    public Task<IActionResult> Reject(
        long id,
        ReviewLeaveQuotaRequest request,
        CancellationToken cancellationToken) =>
        Review(id, false, request, cancellationToken);

    private async Task<IActionResult> Review(
        long id,
        bool approve,
        ReviewLeaveQuotaRequest request,
        CancellationToken cancellationToken)
    {
        if (id <= 0 ||
            string.IsNullOrWhiteSpace(request.ReviewedBy) ||
            string.IsNullOrWhiteSpace(request.ReviewedByName))
            return BadRequest("ข้อมูลผู้ดำเนินการไม่ครบถ้วน");

        var actorEmployeeId = await GetActorEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actorEmployeeId))
            return Unauthorized();
        if (!string.Equals(actorEmployeeId, request.ReviewedBy.Trim(), StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับผู้ดำเนินการ");
        var actorName = await GetEmployeeName(actorEmployeeId, cancellationToken);

        var actionKey = approve ? "APPROVE" : "REJECT";
        var hasPermission = await actionPermissionService.HasPermission(
            actorEmployeeId, "LEAVE_REQUEST_QUOTA", actionKey, cancellationToken);
        if (!hasPermission)
        {
            // VIEW_ALL was the original administrator permission for this page.
            // Keep it as a compatibility fallback for users configured before
            // APPROVE and REJECT were introduced as separate actions.
            hasPermission = await actionPermissionService.HasPermission(
                actorEmployeeId, "LEAVE_REQUEST_QUOTA", "VIEW_ALL", cancellationToken);
        }
        if (!hasPermission)
            return StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์ดำเนินการคำขอเพิ่มโควต้า");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string selectSql = """
            SELECT request.employee_id, request.leave_type_id, request.quota_year,
                   request.requested_hours, request.status, leave_type.default_hours,
                   request.requested_by, leave_type.code
            FROM public.leave_quota_requests request
            JOIN public.leave_types leave_type ON leave_type.id = request.leave_type_id
            WHERE request.id = @id
            FOR UPDATE
            """;
        string employeeId;
        long leaveTypeId;
        short quotaYear;
        decimal requestedHours;
        decimal defaultHours;
        string status;
        string requestedBy;
        string leaveTypeCode;
        await using (var command = new NpgsqlCommand(selectSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return NotFound("ไม่พบคำขอเพิ่มโควต้า");
            employeeId = reader.GetString(0);
            leaveTypeId = reader.GetInt64(1);
            quotaYear = reader.GetInt16(2);
            requestedHours = reader.GetDecimal(3);
            status = reader.GetString(4);
            defaultHours = reader.GetDecimal(5);
            requestedBy = reader.GetString(6);
            leaveTypeCode = reader.GetString(7);
        }

        if (string.Equals(actorEmployeeId, requestedBy, StringComparison.OrdinalIgnoreCase))
            return StatusCode(
                StatusCodes.Status403Forbidden,
                "ไม่สามารถอนุมัติหรือไม่อนุมัติคำขอเพิ่มวันลาที่ตนเองเป็นผู้ยื่นได้");

        if (status != "PENDING")
            return Conflict("ดำเนินการได้เฉพาะคำขอที่อยู่ในสถานะรออนุมัติ");

        var approvedHours = approve ? request.ApprovedHours : null;
        if (approve && (!approvedHours.HasValue || approvedHours <= 0 || approvedHours > requestedHours))
            return BadRequest($"จำนวนที่อนุมัติต้องมากกว่า 0 และไม่เกิน {requestedHours:0.##} ชั่วโมง");
        if (!approve && string.IsNullOrWhiteSpace(request.Remark))
            return BadRequest("กรุณาระบุเหตุผลที่ไม่อนุมัติ");

        const string updateRequestSql = """
            UPDATE public.leave_quota_requests
            SET status = @status,
                approved_hours = @approved_hours,
                reviewed_by = @reviewed_by,
                reviewed_by_name = @reviewed_by_name,
                reviewed_at = CURRENT_TIMESTAMP,
                review_remark = @remark
            WHERE id = @id AND status = 'PENDING'
            """;
        await using (var command = new NpgsqlCommand(updateRequestSql, connection, transaction))
        {
            command.Parameters.AddWithValue("status", approve ? "APPROVED" : "REJECTED");
            command.Parameters.Add(new NpgsqlParameter<decimal?>("approved_hours", approvedHours));
            command.Parameters.AddWithValue("reviewed_by", actorEmployeeId);
            command.Parameters.AddWithValue("reviewed_by_name", actorName);
            command.Parameters.Add(new NpgsqlParameter<string?>("remark", request.Remark?.Trim()));
            command.Parameters.AddWithValue("id", id);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                return Conflict("คำขอนี้ถูกดำเนินการไปแล้ว");
        }

        if (approve)
        {
            var approvalYear = (short)GetBangkokToday().Year;
            var isCrossYearApproval = quotaYear != approvalYear;
            var targetQuotaYear = isCrossYearApproval ? approvalYear : quotaYear;
            var creditedHours = approvedHours!.Value;
            var excessHours = 0m;

            if (isCrossYearApproval)
            {
                const string quotaLockSql = "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))";
                await using (var quotaLockCommand = new NpgsqlCommand(quotaLockSql, connection, transaction))
                {
                    quotaLockCommand.Parameters.AddWithValue(
                        "lock_key", $"LEAVE_QUOTA_ANNUAL:{targetQuotaYear}");
                    await quotaLockCommand.ExecuteNonQueryAsync(cancellationToken);
                    quotaLockCommand.Parameters["lock_key"].Value =
                        $"LEAVE_QUOTA:{employeeId}:{leaveTypeId}:{targetQuotaYear}";
                    await quotaLockCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                const string currentQuotaSql = """
                    SELECT quota_hours
                    FROM public.leave_quotas
                    WHERE employee_id = @employee_id
                      AND leave_type_id = @leave_type_id
                      AND quota_year = @quota_year
                    FOR UPDATE
                    """;
                await using var currentQuotaCommand = new NpgsqlCommand(currentQuotaSql, connection, transaction);
                currentQuotaCommand.Parameters.AddWithValue("employee_id", employeeId);
                currentQuotaCommand.Parameters.AddWithValue("leave_type_id", leaveTypeId);
                currentQuotaCommand.Parameters.AddWithValue("quota_year", targetQuotaYear);
                var currentQuotaValue = await currentQuotaCommand.ExecuteScalarAsync(cancellationToken);
                var currentQuotaHours = currentQuotaValue is null or DBNull ? 0m : Convert.ToDecimal(currentQuotaValue);
                creditedHours = Math.Min(approvedHours.Value, Math.Max(defaultHours - currentQuotaHours, 0m));
                excessHours = approvedHours.Value - creditedHours;
            }

            const string upsertQuotaSql = """
                INSERT INTO public.leave_quotas
                    (employee_id, leave_type_id, quota_year, quota_hours, notes,
                     created_by, created_by_name, updated_by, updated_by_name,
                     annual_excess_hours)
                VALUES
                    (@employee_id, @leave_type_id, @quota_year, @hours, @notes,
                     @action_by, @action_by_name, @action_by, @action_by_name,
                     CASE WHEN @is_vacation THEN GREATEST(@hours - 96, 0) ELSE 0 END)
                ON CONFLICT (employee_id, leave_type_id, quota_year)
                DO UPDATE SET
                    quota_hours = public.leave_quotas.quota_hours + EXCLUDED.quota_hours,
                    annual_excess_hours = CASE WHEN @is_vacation
                        THEN GREATEST(public.leave_quotas.quota_hours + EXCLUDED.quota_hours - 96, 0)
                        ELSE public.leave_quotas.annual_excess_hours END,
                    notes = EXCLUDED.notes,
                    updated_by = EXCLUDED.updated_by,
                    updated_by_name = EXCLUDED.updated_by_name
                RETURNING id, quota_hours
                """;
            long? quotaId = null;
            decimal totalQuotaHours = 0;
            if (creditedHours > 0)
            {
                await using var command = new NpgsqlCommand(upsertQuotaSql, connection, transaction);
                command.Parameters.AddWithValue("employee_id", employeeId);
                command.Parameters.AddWithValue("leave_type_id", leaveTypeId);
                command.Parameters.AddWithValue("quota_year", targetQuotaYear);
                command.Parameters.AddWithValue("hours", creditedHours);
                command.Parameters.AddWithValue("is_vacation",
                    string.Equals(leaveTypeCode, "VACATION", StringComparison.OrdinalIgnoreCase));
                command.Parameters.AddWithValue("notes", $"เพิ่มจากคำขอ {id}");
                command.Parameters.AddWithValue("action_by", actorEmployeeId);
                command.Parameters.AddWithValue("action_by_name", actorName);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                quotaId = reader.GetInt64(0);
                totalQuotaHours = reader.GetDecimal(1);
            }

            if (quotaId.HasValue)
            {
                const string quotaHistorySql = """
                    INSERT INTO public.leave_quota_history
                        (leave_quota_id, action, details_text, after_data,
                         action_by, action_by_name)
                    VALUES
                        (@quota_id, 'UPDATE', @details, CAST(@after_data AS jsonb),
                         @action_by, @action_by_name)
                    """;
                await using var historyCommand = new NpgsqlCommand(quotaHistorySql, connection, transaction);
                historyCommand.Parameters.AddWithValue("quota_id", quotaId.Value);
                historyCommand.Parameters.AddWithValue(
                    "details",
                    $"อนุมัติคำขอเพิ่มโควต้า {approvedHours.Value:0.##} ชั่วโมง; " +
                    $"เพิ่มเข้าโควต้าปี {targetQuotaYear} จำนวน {creditedHours:0.##} ชั่วโมง; " +
                    $"โควต้ารวม {totalQuotaHours:0.##} ชั่วโมง" +
                    (excessHours > 0 ? $"; ส่วนเกิน {excessHours:0.##} ชั่วโมง" : string.Empty));
                historyCommand.Parameters.AddWithValue(
                    "after_data",
                    $"{{\"quotaHours\":{totalQuotaHours.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"\"creditedHours\":{creditedHours.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"\"excessHours\":{excessHours.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
                historyCommand.Parameters.AddWithValue("action_by", actorEmployeeId);
                historyCommand.Parameters.AddWithValue("action_by_name", actorName);
                await historyCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            if (isCrossYearApproval)
            {
                const string excessSql = """
                    INSERT INTO public.leave_quota_excess_details
                        (employee_id, leave_type_id, quota_year, source_type, source_id,
                         source_year, requested_hours, credited_hours, excess_hours, notes)
                    VALUES
                        (@employee_id, @leave_type_id, @quota_year, 'LEAVE_QUOTA_REQUEST', @source_id,
                         @source_year, @requested_hours, @credited_hours, @excess_hours, @notes)
                    ON CONFLICT (source_type, source_id) DO NOTHING
                    """;
                await using var excessCommand = new NpgsqlCommand(excessSql, connection, transaction);
                excessCommand.Parameters.AddWithValue("employee_id", employeeId);
                excessCommand.Parameters.AddWithValue("leave_type_id", leaveTypeId);
                excessCommand.Parameters.AddWithValue("quota_year", targetQuotaYear);
                excessCommand.Parameters.AddWithValue("source_id", id);
                excessCommand.Parameters.AddWithValue("source_year", quotaYear);
                excessCommand.Parameters.AddWithValue("requested_hours", approvedHours.Value);
                excessCommand.Parameters.AddWithValue("credited_hours", creditedHours);
                excessCommand.Parameters.AddWithValue("excess_hours", excessHours);
                excessCommand.Parameters.AddWithValue(
                    "notes", $"คำขอปี {quotaYear} อนุมัติในปี {targetQuotaYear}");
                await excessCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        const string requestHistorySql = """
            INSERT INTO public.leave_quota_request_history
                (leave_quota_request_id, action, details_text, action_by, action_by_name)
            VALUES
                (@request_id, @action, @details, @action_by, @action_by_name)
            """;
        await using (var command = new NpgsqlCommand(requestHistorySql, connection, transaction))
        {
            command.Parameters.AddWithValue("request_id", id);
            command.Parameters.AddWithValue("action", approve ? "APPROVE" : "REJECT");
            command.Parameters.AddWithValue(
                "details",
                approve
                    ? $"อนุมัติเพิ่มโควต้า {approvedHours!.Value:0.##} ชั่วโมง" +
                      (string.IsNullOrWhiteSpace(request.Remark) ? string.Empty : $"; หมายเหตุ: {request.Remark.Trim()}")
                    : $"ไม่อนุมัติคำขอ; เหตุผล: {request.Remark!.Trim()}");
            command.Parameters.AddWithValue("action_by", actorEmployeeId);
            command.Parameters.AddWithValue("action_by_name", actorName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    private async Task<string?> GetActorEmployeeId(CancellationToken cancellationToken)
    {
        var localEmployeeId = User.FindFirstValue("employee_id");
        if (!string.IsNullOrWhiteSpace(localEmployeeId))
            return localEmployeeId.Trim();

        var tenantId = User.FindFirstValue("tid");
        var objectId = User.FindFirstValue("oid");
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId))
            return null;

        const string sql = """
            SELECT employee_id
            FROM public.microsoft_accounts
            WHERE tenant_id = @tenant_id AND entra_object_id = @object_id
              AND is_active = TRUE
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("object_id", objectId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task<ActionResult?> ValidateCommentReadAccess(
        long requestId, CancellationToken cancellationToken)
    {
        var actorEmployeeId = await GetActorEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actorEmployeeId)) return Forbid();

        if (!await LeaveQuotaRequestExists(requestId, cancellationToken)) return NotFound();
        return await pageAccessService.HasAccess(
            actorEmployeeId, "LEAVE_REQUEST_QUOTA", cancellationToken)
            ? null
            : StatusCode(StatusCodes.Status403Forbidden,
                "ไม่มีสิทธิ์ดูความคิดเห็นของคำขอเพิ่มวันลานี้");
    }

    private async Task<ActionResult?> ValidateCommentWriteAccess(
        long requestId, CancellationToken cancellationToken)
    {
        var actorEmployeeId = await GetActorEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actorEmployeeId)) return Forbid();

        const string sql = "SELECT requested_by FROM public.leave_quota_requests WHERE id = @id";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", requestId);
        var requestedBy = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (requestedBy is null) return NotFound();

        if (string.Equals(actorEmployeeId, requestedBy, StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var action in new[] { "APPROVE", "REJECT" })
            if (await actionPermissionService.HasPermission(
                    actorEmployeeId, "LEAVE_REQUEST_QUOTA", action, cancellationToken))
                return null;

        return StatusCode(StatusCodes.Status403Forbidden,
            "คุณมีสิทธิ์อ่านความคิดเห็น แต่ไม่มีสิทธิ์ตอบกลับคำขอนี้");
    }

    private async Task<bool> LeaveQuotaRequestExists(
        long requestId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS(SELECT 1 FROM public.leave_quota_requests WHERE id = @id)");
        command.Parameters.AddWithValue("id", requestId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task<string> GetEmployeeName(
        string employeeId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COALESCE(NULLIF(b.full_name_th, ''), NULLIF(b.full_name_en, ''), e.employee_code)
            FROM public.employees e
            LEFT JOIN public.employee_basic_info b ON b.employee_id = e.id
            WHERE UPPER(BTRIM(e.employee_code)) = UPPER(BTRIM(@employee_id))
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", employeeId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken)
            ?? User.FindFirstValue("name")
            ?? employeeId;
    }

    private static DateOnly GetBangkokToday() =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));

    private async Task<bool> AreDirectReports(
        string managerEmployeeId,
        IReadOnlyCollection<string> employeeIds,
        CancellationToken cancellationToken)
    {
        if (employeeIds.Count == 0)
            return false;

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
                WHERE UPPER(BTRIM(e.employee_code)) = UPPER(BTRIM(@manager_employee_id))
                  AND e.is_active = TRUE
                LIMIT 1
            ), requested AS
            (
                SELECT DISTINCT BTRIM(value) AS employee_code
                FROM UNNEST(@employee_ids::text[]) AS value
                WHERE BTRIM(value) <> ''
            )
            SELECT COUNT(*) = 0
            FROM requested r
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM public.employees e
                JOIN public.employee_company_info c ON c.employee_id = e.id
                CROSS JOIN manager m
                WHERE e.is_active = TRUE
                  AND UPPER(BTRIM(e.employee_code)) = UPPER(r.employee_code)
                  AND e.id <> m.id
                  AND
                  (
                      UPPER(BTRIM(COALESCE(c.supervisor_employee_id, ''))) = UPPER(m.employee_code)
                      OR UPPER(BTRIM(COALESCE(c.leave_approver_employee_id, ''))) = UPPER(m.employee_code)
                      OR REGEXP_REPLACE(UPPER(BTRIM(COALESCE(c.supervisor_name, ''))), '\s+', ' ', 'g') = ANY(m.names)
                      OR REGEXP_REPLACE(UPPER(BTRIM(COALESCE(c.leave_approver_name, ''))), '\s+', ' ', 'g') = ANY(m.names)
                  )
            )
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("manager_employee_id", managerEmployeeId);
        command.Parameters.AddWithValue("employee_ids", employeeIds.ToArray());
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task<LeaveQuotaRequestDto?> FindById(long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT r.id, r.request_no, r.employee_id, r.leave_type_id,
                   t.name_th, r.quota_year, r.requested_hours,
                   r.approved_hours, r.request_reason, r.status, r.requested_by_name,
                   r.requested_at, r.requested_by,
                   (SELECT COUNT(*)::int FROM public.leave_quota_request_comments comment
                    WHERE comment.leave_quota_request_id=r.id AND comment.is_active=TRUE)
            FROM public.leave_quota_requests r
            JOIN public.leave_types t ON t.id = r.leave_type_id
            WHERE r.id = @id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new LeaveQuotaRequestDto(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt64(3), reader.GetString(4), reader.GetInt16(5),
            reader.GetDecimal(6), reader.IsDBNull(7) ? null : reader.GetDecimal(7),
            reader.GetString(8), reader.GetString(9),
            reader.GetString(10), reader.GetFieldValue<DateTimeOffset>(11))
            { RequestedBy = reader.GetString(12), CommentCount = reader.GetInt32(13) };
    }

    private async Task<IReadOnlyList<LeaveCommentAttachmentDto>> LoadCommentAttachments(long commentId,CancellationToken cancellationToken)
    {
        var result=new List<LeaveCommentAttachmentDto>();
        await using var command=dataSource.CreateCommand("""
            SELECT id,original_file_name,content_type,file_size_bytes,uploaded_at
            FROM public.leave_quota_request_comment_attachments
            WHERE leave_quota_request_comment_id=@comment_id ORDER BY uploaded_at,id
            """);
        command.Parameters.AddWithValue("comment_id",commentId);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken))
            result.Add(new LeaveCommentAttachmentDto(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),reader.GetInt64(3),reader.GetFieldValue<DateTimeOffset>(4)));
        return result;
    }

    private static string? ValidateCommentAttachments(IReadOnlyList<LeaveCommentAttachmentUploadDto>? attachments)
    {
        if((attachments?.Count??0)>5)return "แนบรูปได้ไม่เกิน 5 รูปต่อ Comment";
        foreach(var attachment in attachments??[])
            if(string.IsNullOrWhiteSpace(attachment.FileName)||attachment.ContentType.ToLowerInvariant() is not ("image/png" or "image/jpeg" or "image/webp")||attachment.Content is null||attachment.Content.Length is < 1 or > 3145728)
                return "รองรับเฉพาะ PNG, JPG และ WEBP ขนาดไม่เกิน 3 MB";
        return null;
    }
}
