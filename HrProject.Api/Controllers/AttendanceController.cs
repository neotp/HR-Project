using HrProject.Api.Services;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/attendance")]
public sealed class AttendanceController(
    NpgsqlDataSource dataSource,
    PageAccessService pageAccessService,
    PageActionPermissionService actionPermissionService,
    AttendanceReviewEmailNotificationService emailNotificationService,
    ILogger<AttendanceController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AttendanceDailyDto>>> GetDailyRecords(
        [FromQuery] string employeeId,
        [FromQuery] DateOnly? startDate,
        [FromQuery] DateOnly? endDate,
        CancellationToken cancellationToken)
    {
        var error = await ValidateOwnEmployee(employeeId, cancellationToken);
        if (error is not null) return error;
        var from = startDate ?? new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        var to = endDate ?? from.AddMonths(1).AddDays(-1);
        if (to < from || to.DayNumber - from.DayNumber > 366)
            return BadRequest("ช่วงวันที่ต้องไม่เกิน 366 วัน");

        const string sql = """
            SELECT id, employee_id, work_date, first_scan_at, last_scan_at,
                   scan_count, calculated_status, final_status, late_minutes,
                   missing_minutes, requires_review, review_reason,
                   calculated_at, override_reason,
                   (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Bangkok') < schedule.work_end_at,
                   (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Bangkok') >= schedule.work_end_at
                     AND (daily.calculated_at AT TIME ZONE 'Asia/Bangkok') >= schedule.work_end_at
            FROM public.attendance_daily_records daily
            CROSS JOIN LATERAL
            (
                SELECT daily.work_date + CASE
                    WHEN EXISTS
                    (
                        SELECT 1 FROM public.work_calendar_days calendar
                        WHERE calendar.calendar_date = daily.work_date
                          AND calendar.day_type = 'WORKING_SATURDAY'
                    ) THEN TIME '17:00'
                    ELSE TIME '18:00'
                END AS work_end_at
            ) schedule
            WHERE daily.employee_id = @employee_id AND daily.work_date BETWEEN @start_date AND @end_date
              AND NOT EXISTS
              (
                  SELECT 1 FROM public.work_calendar_days calendar
                  WHERE calendar.calendar_date = daily.work_date
                    AND calendar.day_type = 'PUBLIC_HOLIDAY'
              )
              AND
              (
                  EXTRACT(ISODOW FROM daily.work_date) BETWEEN 1 AND 5
                  OR EXISTS
                  (
                      SELECT 1 FROM public.work_calendar_days calendar
                      WHERE calendar.calendar_date = daily.work_date
                        AND calendar.day_type = 'WORKING_SATURDAY'
                  )
              )
            ORDER BY daily.work_date
            """;
        var result = new List<AttendanceDailyDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", employeeId.Trim());
        command.Parameters.AddWithValue("start_date", from);
        command.Parameters.AddWithValue("end_date", to);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AttendanceDailyDto(
                reader.GetInt64(0), reader.GetString(1), reader.GetFieldValue<DateOnly>(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4), reader.GetInt32(5),
                reader.GetString(6), reader.GetString(7), reader.GetInt32(8),
                reader.GetInt32(9), reader.GetBoolean(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetFieldValue<DateTimeOffset>(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetBoolean(14), reader.GetBoolean(15)));
        }
        return Ok(result);
    }

    [HttpGet("{id:long}/history")]
    public async Task<ActionResult<IReadOnlyList<AttendanceHistoryDto>>> GetHistory(
        long id, CancellationToken cancellationToken)
    {
        const string ownerSql = "SELECT employee_id FROM public.attendance_daily_records WHERE id = @id";
        await using var ownerCommand = dataSource.CreateCommand(ownerSql);
        ownerCommand.Parameters.AddWithValue("id", id);
        var employeeId = (string?)await ownerCommand.ExecuteScalarAsync(cancellationToken);
        if (employeeId is null) return NotFound();
        var error = await ValidateOwnEmployee(employeeId, cancellationToken);
        if (error is not null) return error;

        const string sql = """
            SELECT id, action, status_before, status_after, details,
                   action_by, action_by_name, action_at
            FROM public.attendance_daily_history
            WHERE attendance_daily_id = @id
            ORDER BY action_at DESC, id DESC
            """;
        var result = new List<AttendanceHistoryDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new AttendanceHistoryDto(
                reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7)));
        return Ok(result);
    }

    [HttpGet("{id:long}/comments")]
    public async Task<ActionResult<IReadOnlyList<AttendanceCommentDto>>> GetComments(
        long id, CancellationToken cancellationToken)
    {
        var accessError = await ValidateOwnOrReviewerRecord(id, cancellationToken);
        if (accessError is not null) return accessError;

        const string sql = """
            SELECT id, attendance_daily_id, comment_text, commented_by,
                   commented_by_name, commented_at
            FROM public.attendance_comments
            WHERE attendance_daily_id = @id AND is_active = TRUE
            ORDER BY commented_at, id
            """;
        var result = new List<AttendanceCommentDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new AttendanceCommentDto(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
        return Ok(result);
    }

    [HttpPost("{id:long}/comments")]
    public async Task<ActionResult<AttendanceCommentDto>> AddComment(
        long id, [FromBody] AddAttendanceCommentRequest request,
        CancellationToken cancellationToken)
    {
        var accessError = await ValidateOwnOrReviewerRecord(id, cancellationToken);
        if (accessError is not null) return accessError;

        var actor = await GetAuthenticatedEmployee(cancellationToken);
        if (actor is null) return Unauthorized();
        var commentText = request.CommentText?.Trim();
        if (string.IsNullOrWhiteSpace(commentText))
            return BadRequest("กรุณากรอกความคิดเห็น");
        if (commentText.Length > 4000)
            return BadRequest("ความคิดเห็นต้องไม่เกิน 4,000 ตัวอักษร");

        const string sql = """
            INSERT INTO public.attendance_comments
                (attendance_daily_id, comment_text, commented_by, commented_by_name)
            VALUES (@daily_id, @comment_text, @commented_by, @commented_by_name)
            RETURNING id, commented_at
            """;
        long commentId;
        DateTimeOffset commentedAt;
        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("daily_id", id);
            command.Parameters.AddWithValue("comment_text", commentText);
            command.Parameters.AddWithValue("commented_by", actor.Value.EmployeeId);
            command.Parameters.AddWithValue("commented_by_name", actor.Value.Name);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            commentId = reader.GetInt64(0);
            commentedAt = reader.GetFieldValue<DateTimeOffset>(1);
        }

        return Ok(new AttendanceCommentDto(commentId, id, commentText,
            actor.Value.EmployeeId, actor.Value.Name, commentedAt));
    }

    [HttpGet("{id:long}/responses")]
    public async Task<ActionResult<IReadOnlyList<AttendanceResponseDto>>> GetResponses(
        long id, CancellationToken cancellationToken)
    {
        var ownerError = await ValidateOwnRecord(id, cancellationToken);
        if (ownerError is not null) return ownerError;

        const string sql = """
            SELECT id, response_text, status, submitted_by, submitted_by_name, submitted_at
            FROM public.attendance_responses
            WHERE attendance_daily_id = @id
            ORDER BY submitted_at DESC, id DESC
            """;
        var rows = new List<(long Id, string Text, string Status, string By, string ByName, DateTimeOffset At)>();
        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
        }

        var result = new List<AttendanceResponseDto>(rows.Count);
        foreach (var row in rows)
        {
            var attachments = await LoadResponseAttachments(row.Id, cancellationToken);
            result.Add(new AttendanceResponseDto(row.Id, id, row.Text, row.Status,
                row.By, row.ByName, row.At, attachments)
            {
                Issues = await LoadResponseIssues(row.Id, cancellationToken)
            });
        }
        return Ok(result);
    }

    [HttpGet("responses/history")]
    public async Task<ActionResult<IReadOnlyList<AttendanceResponseHistoryItemDto>>> GetResponseHistory(
        CancellationToken cancellationToken)
    {
        var actor = await GetAuthenticatedEmployee(cancellationToken);
        if (actor is null) return Unauthorized();

        const string sql = """
            SELECT response.id, response.attendance_daily_id, response.response_text,
                   response.status, response.submitted_by, response.submitted_by_name,
                   response.submitted_at, daily.work_date, daily.first_scan_at,
                   daily.last_scan_at, daily.final_status, daily.late_minutes,
                   daily.missing_minutes
            FROM public.attendance_responses response
            JOIN public.attendance_daily_records daily
              ON daily.id = response.attendance_daily_id
            WHERE daily.employee_id = @employee_id
            ORDER BY response.submitted_at DESC, response.id DESC
            """;
        var rows = new List<(long ResponseId, long DailyId, string Text, string Status,
            string SubmittedBy, string SubmittedByName, DateTimeOffset SubmittedAt,
            DateOnly WorkDate, DateTime? FirstScanAt, DateTime? LastScanAt,
            string AttendanceStatus, int LateMinutes, int MissingMinutes)>();
        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("employee_id", actor.Value.EmployeeId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateOnly>(7),
                    reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    reader.IsDBNull(9) ? null : reader.GetDateTime(9), reader.GetString(10),
                    reader.GetInt32(11), reader.GetInt32(12)));
        }

        if (rows.Count == 0) return Ok(Array.Empty<AttendanceResponseHistoryItemDto>());
        var responseIds = rows.Select(row => row.ResponseId).ToArray();
        var attachments = await LoadResponseAttachments(responseIds, cancellationToken);
        var issues = await LoadResponseIssues(responseIds, cancellationToken);
        var result = rows.Select(row => new AttendanceResponseHistoryItemDto(
            row.WorkDate, row.FirstScanAt, row.LastScanAt, row.AttendanceStatus,
            row.LateMinutes, row.MissingMinutes,
            new AttendanceResponseDto(row.ResponseId, row.DailyId, row.Text, row.Status,
                row.SubmittedBy, row.SubmittedByName, row.SubmittedAt,
                attachments.GetValueOrDefault(row.ResponseId) ?? [])
            {
                Issues = issues.GetValueOrDefault(row.ResponseId) ?? []
            })).ToList();
        return Ok(result);
    }

    [HttpPost("{id:long}/responses")]
    public async Task<ActionResult<AttendanceResponseDto>> SubmitResponse(
        long id, AttendanceResponseRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ResponseText))
            return BadRequest("กรุณากรอกข้อโต้แย้ง");
        if (request.ResponseText.Trim().Length > 4000)
            return BadRequest("ข้อโต้แย้งต้องไม่เกิน 4,000 ตัวอักษร");
        var attachmentError = ValidateAttachments(request.Attachments);
        if (attachmentError is not null) return BadRequest(attachmentError);

        var ownerError = await ValidateOwnRecord(id, cancellationToken);
        if (ownerError is not null) return ownerError;
        var responseBlockReason = await GetResponseBlockReason(id, cancellationToken);
        if (responseBlockReason is not null)
            return Conflict(responseBlockReason);
        var actor = await GetAuthenticatedEmployee(cancellationToken);
        if (actor is null) return Unauthorized();

        var availableIssueTypes = await GetAvailableIssueTypes(id, cancellationToken);
        var requestedIssueTypes = NormalizeIssueTypes(request.IssueTypes);
        if (requestedIssueTypes.Count == 0) requestedIssueTypes = availableIssueTypes;
        if (requestedIssueTypes.Count == 0 || requestedIssueTypes.Any(issue => !availableIssueTypes.Contains(issue)))
            return BadRequest("ประเภทข้อโต้แย้งไม่ตรงกับผลการมาทำงานของรายการนี้");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string insertSql = """
                INSERT INTO public.attendance_responses
                    (attendance_daily_id, response_text, submitted_by, submitted_by_name)
                VALUES (@daily_id, @response_text, @submitted_by, @submitted_by_name)
                RETURNING id, submitted_at
                """;
            long responseId;
            DateTimeOffset submittedAt;
            await using (var command = new NpgsqlCommand(insertSql, connection, transaction))
            {
                command.Parameters.AddWithValue("daily_id", id);
                command.Parameters.AddWithValue("response_text", request.ResponseText.Trim());
                command.Parameters.AddWithValue("submitted_by", actor.Value.EmployeeId);
                command.Parameters.AddWithValue("submitted_by_name", actor.Value.Name);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                responseId = reader.GetInt64(0);
                submittedAt = reader.GetFieldValue<DateTimeOffset>(1);
            }

            foreach (var issueType in requestedIssueTypes)
            {
                await using var issueCommand = new NpgsqlCommand("""
                    INSERT INTO public.attendance_response_issues(attendance_response_id, issue_type)
                    VALUES (@response_id, @issue_type)
                    """, connection, transaction);
                issueCommand.Parameters.AddWithValue("response_id", responseId);
                issueCommand.Parameters.AddWithValue("issue_type", issueType);
                await issueCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var attachment in request.Attachments ?? [])
            {
                const string attachmentSql = """
                    INSERT INTO public.attendance_response_attachments
                        (attendance_response_id, original_file_name, content_type,
                         file_size_bytes, file_content)
                    VALUES (@response_id, @file_name, @content_type, @file_size, @file_content)
                    """;
                await using var command = new NpgsqlCommand(attachmentSql, connection, transaction);
                command.Parameters.AddWithValue("response_id", responseId);
                command.Parameters.AddWithValue("file_name", Path.GetFileName(attachment.FileName));
                command.Parameters.AddWithValue("content_type", string.IsNullOrWhiteSpace(attachment.ContentType)
                    ? "application/octet-stream" : attachment.ContentType);
                command.Parameters.AddWithValue("file_size", attachment.Content.LongLength);
                command.Parameters.AddWithValue("file_content", NpgsqlDbType.Bytea, attachment.Content);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string markReviewSql = """
                UPDATE public.attendance_daily_records
                SET requires_review = TRUE,
                    review_reason = 'มีข้อโต้แย้งรอตรวจสอบ',
                    updated_at = CURRENT_TIMESTAMP
                WHERE id = @daily_id
                """;
            await using (var command = new NpgsqlCommand(markReviewSql, connection, transaction))
            {
                command.Parameters.AddWithValue("daily_id", id);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string historySql = """
                INSERT INTO public.attendance_daily_history
                    (attendance_daily_id, action, status_before, status_after,
                     details, action_by, action_by_name)
                SELECT id, 'RESPONSE_SUBMITTED', final_status, final_status,
                       @details, @action_by, @action_by_name
                FROM public.attendance_daily_records WHERE id = @daily_id
                """;
            await using (var command = new NpgsqlCommand(historySql, connection, transaction))
            {
                command.Parameters.AddWithValue("daily_id", id);
                command.Parameters.AddWithValue("details", request.ResponseText.Trim());
                command.Parameters.AddWithValue("action_by", actor.Value.EmployeeId);
                command.Parameters.AddWithValue("action_by_name", actor.Value.Name);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            try
            {
                await emailNotificationService.NotifySubmittedAsync(id, cancellationToken);
            }
            catch (Exception exception)
            {
                // The dispute is already committed. Email delivery must not turn a
                // successful submission into an error or duplicate on client retry.
                logger.LogError(exception,
                    "Attendance dispute {AttendanceDailyId} was saved but email notification failed", id);
            }
            var attachments = await LoadResponseAttachments(responseId, cancellationToken);
            return Ok(new AttendanceResponseDto(responseId, id, request.ResponseText.Trim(),
                "SUBMITTED", actor.Value.EmployeeId, actor.Value.Name, submittedAt, attachments)
            {
                Issues = await LoadResponseIssues(responseId, cancellationToken)
            });
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("รายการนี้มีข้อโต้แย้งที่กำลังรอดำเนินการอยู่แล้ว");
        }
    }

    [HttpGet("{id:long}/responses/{responseId:long}/attachments/{attachmentId:long}/preview")]
    public async Task<IActionResult> PreviewResponseAttachment(
        long id, long responseId, long attachmentId, CancellationToken cancellationToken)
    {
        var ownerError = await ValidateOwnOrReviewerRecord(id, cancellationToken);
        if (ownerError is not null) return ownerError;
        const string sql = """
            SELECT attachment.content_type, attachment.file_content
            FROM public.attendance_response_attachments attachment
            JOIN public.attendance_responses response
              ON response.id = attachment.attendance_response_id
            WHERE attachment.id = @attachment_id AND response.id = @response_id
              AND response.attendance_daily_id = @daily_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("attachment_id", attachmentId);
        command.Parameters.AddWithValue("response_id", responseId);
        command.Parameters.AddWithValue("daily_id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return NotFound();
        var contentType = reader.GetString(0);
        var content = reader.GetFieldValue<byte[]>(1);
        return File(content, contentType);
    }

    [HttpGet("reviews")]
    public async Task<ActionResult<IReadOnlyList<AttendanceReviewItemDto>>> GetReviewItems(
        [FromQuery] DateOnly? startDate,
        [FromQuery] DateOnly? endDate,
        CancellationToken cancellationToken)
    {
        var actor = await GetAuthenticatedEmployee(cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await pageAccessService.HasAccess(actor.Value.EmployeeId, "ATTENDANCE_REVIEWS", cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์เข้าหน้าตรวจสอบข้อโต้แย้งการมาทำงาน");

        var from = startDate ?? new DateOnly(DateTime.Today.Year, 1, 1);
        var to = endDate ?? DateOnly.FromDateTime(DateTime.Today);
        if (to < from || to.DayNumber - from.DayNumber > 366)
            return BadRequest("ช่วงวันที่ต้องไม่เกิน 366 วัน");

        const string sql = """
            SELECT daily.id, daily.employee_id,
                   COALESCE(NULLIF(basic.full_name_th, ''), NULLIF(basic.full_name_en, ''), daily.employee_id),
                   COALESCE(company.department, ''), daily.work_date,
                   daily.first_scan_at, daily.last_scan_at,
                   daily.calculated_status, daily.final_status,
                   daily.calculated_late_minutes, daily.calculated_missing_minutes,
                   daily.late_minutes, daily.missing_minutes,
                   daily.requires_review, daily.review_reason,
                   response.id, response.response_text, response.status,
                   response.submitted_by, response.submitted_by_name, response.submitted_at
            FROM public.attendance_daily_records daily
            JOIN public.employees employee
              ON employee.employee_code = daily.employee_id AND employee.is_active = TRUE
            LEFT JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
            LEFT JOIN public.employee_company_info company ON company.employee_id = employee.id
            LEFT JOIN LATERAL
            (
                SELECT id, response_text, status, submitted_by, submitted_by_name, submitted_at
                FROM public.attendance_responses
                WHERE attendance_daily_id = daily.id
                ORDER BY submitted_at DESC, id DESC
                LIMIT 1
            ) response ON TRUE
            WHERE daily.work_date BETWEEN @start_date AND @end_date
              AND COALESCE(company.exclude_attendance_calculation, FALSE) = FALSE
              AND NOT EXISTS
              (
                  SELECT 1 FROM public.work_calendar_days calendar
                  WHERE calendar.calendar_date = daily.work_date
                    AND calendar.day_type = 'PUBLIC_HOLIDAY'
              )
              AND
              (
                  EXTRACT(ISODOW FROM daily.work_date) BETWEEN 1 AND 5
                  OR EXISTS
                  (
                      SELECT 1 FROM public.work_calendar_days calendar
                      WHERE calendar.calendar_date = daily.work_date
                        AND calendar.day_type = 'WORKING_SATURDAY'
                  )
              )
              AND
              (
                  daily.calculated_late_minutes > 0
                  OR daily.calculated_missing_minutes > 0
                  OR daily.requires_review = TRUE
              )
            ORDER BY CASE WHEN daily.requires_review THEN 0 ELSE 1 END,
                     CASE WHEN response.status = 'SUBMITTED' THEN 0 ELSE 1 END,
                     daily.work_date DESC,
                     daily.employee_id
            """;
        var rows = new List<(long DailyId, string EmployeeId, string EmployeeName, string Department,
            DateOnly WorkDate, DateTime? First, DateTime? Last, string CalculatedStatus, string FinalStatus,
            int Late, int Missing, int FinalLate, int FinalMissing, bool RequiresReview, string? ReviewReason,
            long? ResponseId, string? ResponseText, string? ResponseStatus,
            string? SubmittedBy, string? SubmittedByName, DateTimeOffset? SubmittedAt)>();
        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("start_date", from);
            command.Parameters.AddWithValue("end_date", to);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetFieldValue<DateOnly>(4), reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                    reader.IsDBNull(6) ? null : reader.GetDateTime(6), reader.GetString(7), reader.GetString(8),
                    reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12),
                    reader.GetBoolean(13), reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetInt64(15),
                    reader.IsDBNull(16) ? null : reader.GetString(16),
                    reader.IsDBNull(17) ? null : reader.GetString(17),
                    reader.IsDBNull(18) ? null : reader.GetString(18),
                    reader.IsDBNull(19) ? null : reader.GetString(19),
                    reader.IsDBNull(20) ? null : reader.GetFieldValue<DateTimeOffset>(20)));
        }

        var result = new List<AttendanceReviewItemDto>(rows.Count);
        foreach (var row in rows)
        {
            AttendanceResponseDto? response = null;
            if (row.ResponseId.HasValue)
                response = new AttendanceResponseDto(row.ResponseId.Value, row.DailyId,
                    row.ResponseText!, row.ResponseStatus!, row.SubmittedBy!, row.SubmittedByName!,
                    row.SubmittedAt!.Value,
                    await LoadResponseAttachments(row.ResponseId.Value, cancellationToken))
                {
                    Issues = await LoadResponseIssues(row.ResponseId.Value, cancellationToken)
                };
            result.Add(new AttendanceReviewItemDto(row.DailyId, row.EmployeeId, row.EmployeeName,
                row.Department, row.WorkDate, row.First, row.Last, row.CalculatedStatus, row.FinalStatus,
                row.Late, row.Missing, row.FinalLate, row.FinalMissing,
                row.RequiresReview, row.ReviewReason, response));
        }
        return Ok(result);
    }

    [HttpPost("reviews/{responseId:long}/decision")]
    public async Task<IActionResult> ReviewResponse(
        long responseId, ReviewAttendanceResponseRequest request, CancellationToken cancellationToken)
    {
        var decision = request.Decision?.Trim().ToUpperInvariant();
        if (decision is not ("APPROVE" or "REJECT")) return BadRequest("ผลการตรวจสอบไม่ถูกต้อง");
        if (request.ReviewNote?.Length > 2000) return BadRequest("หมายเหตุต้องไม่เกิน 2,000 ตัวอักษร");

        var actor = await GetAuthenticatedEmployee(cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await pageAccessService.HasAccess(actor.Value.EmployeeId, "ATTENDANCE_REVIEWS", cancellationToken) ||
            !await actionPermissionService.HasPermission(actor.Value.EmployeeId, "ATTENDANCE_REVIEWS", decision, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์ดำเนินการรายการนี้");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        long dailyId;
        string statusBefore;
        string responseStatus;
        int currentLateMinutes;
        int currentMissingMinutes;
        const string lockSql = """
            SELECT response.attendance_daily_id, response.status, daily.final_status,
                   daily.calculated_late_minutes, daily.calculated_missing_minutes,
                   daily.late_minutes, daily.missing_minutes
            FROM public.attendance_responses response
            JOIN public.attendance_daily_records daily ON daily.id = response.attendance_daily_id
            WHERE response.id = @response_id
            FOR UPDATE OF response, daily
            """;
        await using (var command = new NpgsqlCommand(lockSql, connection, transaction))
        {
            command.Parameters.AddWithValue("response_id", responseId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return NotFound();
            dailyId = reader.GetInt64(0);
            responseStatus = reader.GetString(1);
            statusBefore = reader.GetString(2);
            currentLateMinutes = reader.GetInt32(5);
            currentMissingMinutes = reader.GetInt32(6);
        }
        if (responseStatus != "SUBMITTED")
            return Conflict(responseStatus == "APPROVED" ? "ข้อโต้แย้งนี้อนุมัติแล้ว" : "ข้อโต้แย้งนี้ดำเนินการแล้ว");
        var responseBlockReason = await GetResponseBlockReason(dailyId, cancellationToken, blockPresent: false);
        if (responseBlockReason is not null)
            return Conflict(responseBlockReason);

        var pendingIssueTypes = new List<string>();
        await using (var command = new NpgsqlCommand("""
            SELECT issue_type FROM public.attendance_response_issues
            WHERE attendance_response_id=@response_id AND status='SUBMITTED'
            ORDER BY id FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("response_id", responseId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) pendingIssueTypes.Add(reader.GetString(0));
        }
        var selectedIssueTypes = NormalizeIssueTypes(request.IssueTypes);
        if (selectedIssueTypes.Count == 0) selectedIssueTypes = pendingIssueTypes;
        if (selectedIssueTypes.Count == 0 || selectedIssueTypes.Any(issue => !pendingIssueTypes.Contains(issue)))
            return BadRequest("กรุณาเลือกประเภทข้อโต้แย้งที่ยังรอตรวจสอบ");

        await using (var command = new NpgsqlCommand("""
            UPDATE public.attendance_response_issues
            SET status=@status, reviewed_by=@reviewed_by, reviewed_by_name=@reviewed_by_name,
                reviewed_at=CURRENT_TIMESTAMP, review_note=@review_note, updated_at=CURRENT_TIMESTAMP
            WHERE attendance_response_id=@response_id AND status='SUBMITTED'
              AND issue_type=ANY(@issue_types)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("status", decision == "APPROVE" ? "APPROVED" : "REJECTED");
            command.Parameters.AddWithValue("reviewed_by", actor.Value.EmployeeId);
            command.Parameters.AddWithValue("reviewed_by_name", actor.Value.Name);
            command.Parameters.Add(new NpgsqlParameter<string?>("review_note", string.IsNullOrWhiteSpace(request.ReviewNote) ? null : request.ReviewNote.Trim()));
            command.Parameters.AddWithValue("response_id", responseId);
            command.Parameters.AddWithValue("issue_types", selectedIssueTypes.ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        int pendingCount;
        int approvedCount;
        int rejectedCount;
        await using (var command = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE status='SUBMITTED'),
                   count(*) FILTER (WHERE status='APPROVED'),
                   count(*) FILTER (WHERE status='REJECTED')
            FROM public.attendance_response_issues
            WHERE attendance_response_id=@response_id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("response_id", responseId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            pendingCount = checked((int)reader.GetInt64(0));
            approvedCount = checked((int)reader.GetInt64(1));
            rejectedCount = checked((int)reader.GetInt64(2));
        }

        var finalLateMinutes = decision == "APPROVE" && selectedIssueTypes.Contains("LATE")
            ? 0 : currentLateMinutes;
        var finalMissingMinutes = decision == "APPROVE" && selectedIssueTypes.Contains("ABSENT")
            ? 0 : currentMissingMinutes;
        var finalStatus = finalMissingMinutes > 0 ? "ABSENT" : finalLateMinutes > 0 ? "LATE" : "PRESENT";
        var issueResults = new List<(string Type, string Status)>();
        await using (var command = new NpgsqlCommand("""
            SELECT issue_type, status FROM public.attendance_response_issues
            WHERE attendance_response_id=@response_id ORDER BY id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("response_id", responseId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                issueResults.Add((reader.GetString(0), reader.GetString(1)));
        }
        var noteParts = new List<string>();
        var approvedLabels = issueResults.Where(item => item.Status == "APPROVED").Select(item => IssueTypeText(item.Type)).ToList();
        var rejectedLabels = issueResults.Where(item => item.Status == "REJECTED").Select(item => IssueTypeText(item.Type)).ToList();
        var pendingLabels = issueResults.Where(item => item.Status == "SUBMITTED").Select(item => IssueTypeText(item.Type)).ToList();
        if (approvedLabels.Count > 0) noteParts.Add($"อนุมัติข้อโต้แย้งสำหรับ{string.Join(" และ ", approvedLabels)}");
        if (rejectedLabels.Count > 0) noteParts.Add($"ไม่อนุมัติข้อโต้แย้งสำหรับ{string.Join(" และ ", rejectedLabels)}");
        if (pendingLabels.Count > 0) noteParts.Add($"รอตรวจสอบข้อโต้แย้งสำหรับ{string.Join(" และ ", pendingLabels)}");
        var remaining = new List<string>();
        if (finalMissingMinutes > 0) remaining.Add("ขาดงาน");
        if (finalLateMinutes > 0) remaining.Add("มาสาย");
        var resultNote = string.Join("; ", noteParts) + (remaining.Count > 0
            ? $" — ยังคงสถานะ{string.Join(", ", remaining)}" : " — ผลปัจจุบันมาปกติ");
        if (!string.IsNullOrWhiteSpace(request.ReviewNote)) resultNote += $": {request.ReviewNote.Trim()}";
        var overallStatus = pendingCount > 0 ? "SUBMITTED"
            : approvedCount > 0 && rejectedCount > 0 ? "PARTIALLY_APPROVED"
            : approvedCount > 0 ? "APPROVED" : "REJECTED";

        await using (var command = new NpgsqlCommand("""
            UPDATE public.attendance_responses
            SET status=@status, reviewed_by=@reviewed_by, reviewed_by_name=@reviewed_by_name,
                reviewed_at=CURRENT_TIMESTAMP, review_note=@review_note, updated_at=CURRENT_TIMESTAMP
            WHERE id=@response_id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("status", overallStatus);
            command.Parameters.AddWithValue("reviewed_by", actor.Value.EmployeeId);
            command.Parameters.AddWithValue("reviewed_by_name", actor.Value.Name);
            command.Parameters.AddWithValue("review_note", resultNote);
            command.Parameters.AddWithValue("response_id", responseId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new NpgsqlCommand("""
            UPDATE public.attendance_daily_records
            SET final_status=@final_status, late_minutes=@late_minutes,
                missing_minutes=@missing_minutes, requires_review=@requires_review,
                review_reason=@review_reason,
                overridden_by=CASE WHEN @is_approval THEN @reviewed_by ELSE overridden_by END,
                overridden_by_name=CASE WHEN @is_approval THEN @reviewed_by_name ELSE overridden_by_name END,
                overridden_at=CASE WHEN @is_approval THEN CURRENT_TIMESTAMP ELSE overridden_at END,
                override_reason=CASE WHEN @has_approval THEN @review_reason ELSE override_reason END,
                updated_at=CURRENT_TIMESTAMP
            WHERE id=@daily_id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("final_status", finalStatus);
            command.Parameters.AddWithValue("late_minutes", finalLateMinutes);
            command.Parameters.AddWithValue("missing_minutes", finalMissingMinutes);
            command.Parameters.AddWithValue("requires_review", pendingCount > 0);
            command.Parameters.AddWithValue("review_reason", resultNote);
            command.Parameters.AddWithValue("has_approval", approvedCount > 0);
            command.Parameters.AddWithValue("is_approval", decision == "APPROVE");
            command.Parameters.AddWithValue("reviewed_by", actor.Value.EmployeeId);
            command.Parameters.AddWithValue("reviewed_by_name", actor.Value.Name);
            command.Parameters.AddWithValue("daily_id", dailyId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string historySql = """
            INSERT INTO public.attendance_daily_history
                (attendance_daily_id, action, status_before, status_after,
                 details, action_by, action_by_name)
            VALUES (@daily_id, @action, @status_before, @status_after,
                    @details, @action_by, @action_by_name)
            """;
        await using (var command = new NpgsqlCommand(historySql, connection, transaction))
        {
            command.Parameters.AddWithValue("daily_id", dailyId);
            command.Parameters.AddWithValue("action", decision == "APPROVE" ? "RESPONSE_APPROVED" : "RESPONSE_REJECTED");
            command.Parameters.AddWithValue("status_before", statusBefore);
            command.Parameters.AddWithValue("status_after", finalStatus);
            command.Parameters.AddWithValue("details", resultNote);
            command.Parameters.AddWithValue("action_by", actor.Value.EmployeeId);
            command.Parameters.AddWithValue("action_by_name", actor.Value.Name);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    private async Task<ActionResult?> ValidateOwnRecord(long id, CancellationToken cancellationToken)
    {
        const string sql = "SELECT employee_id FROM public.attendance_daily_records WHERE id = @id";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        var employeeId = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return employeeId is null ? NotFound() : await ValidateOwnEmployee(employeeId, cancellationToken);
    }

    private async Task<string?> GetResponseBlockReason(long id, CancellationToken cancellationToken, bool blockPresent = true)
    {
        const string sql = """
            SELECT daily.final_status,
                   (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Bangkok') < schedule.work_end_at,
                   (daily.calculated_at AT TIME ZONE 'Asia/Bangkok') < schedule.work_end_at
            FROM public.attendance_daily_records daily
            CROSS JOIN LATERAL
            (
                SELECT daily.work_date + CASE
                    WHEN EXISTS
                    (
                        SELECT 1 FROM public.work_calendar_days calendar
                        WHERE calendar.calendar_date = daily.work_date
                          AND calendar.day_type = 'WORKING_SATURDAY'
                    ) THEN TIME '17:00'
                    ELSE TIME '18:00'
                END AS work_end_at
            ) schedule
            WHERE daily.id = @id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (reader.GetBoolean(1))
            return "รายการที่อยู่ระหว่างวันทำงานยังไม่สามารถส่งข้อโต้แย้งได้";
        if (reader.GetBoolean(2))
            return "รายการนี้ยังรอคำนวณผลการมาทำงานหลังจบวัน";
        return reader.GetString(0) switch
        {
            "IN_PROGRESS" => "รายการที่อยู่ระหว่างวันทำงานยังไม่สามารถส่งข้อโต้แย้งได้",
            "PRESENT" when blockPresent => "รายการมาปกติไม่สามารถส่งข้อโต้แย้งได้",
            _ => null
        };
    }

    private async Task<ActionResult?> ValidateOwnOrReviewerRecord(
        long id, CancellationToken cancellationToken)
    {
        const string sql = "SELECT employee_id FROM public.attendance_daily_records WHERE id = @id";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        var ownerEmployeeId = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (ownerEmployeeId is null) return NotFound();
        var actor = await GetAuthenticatedEmployee(cancellationToken);
        if (actor is null) return Unauthorized();
        if (string.Equals(actor.Value.EmployeeId, ownerEmployeeId, StringComparison.OrdinalIgnoreCase))
            return null;
        return await pageAccessService.HasAccess(
            actor.Value.EmployeeId, "ATTENDANCE_REVIEWS", cancellationToken)
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์เข้าถึงข้อมูลข้อโต้แย้งนี้");
    }

    private async Task<(string EmployeeId, string Name)?> GetAuthenticatedEmployee(
        CancellationToken cancellationToken)
    {
        var directEmployeeId = User.FindFirst("employee_id")?.Value;
        if (!string.IsNullOrWhiteSpace(directEmployeeId))
            return (directEmployeeId,
                User.FindFirst("name")?.Value ?? directEmployeeId);

        var tenantId = User.FindFirst("tid")?.Value;
        var objectId = User.FindFirst("oid")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId)) return null;
        const string sql = """
            SELECT employee_id, COALESCE(NULLIF(display_name, ''), employee_id)
            FROM public.microsoft_accounts
            WHERE tenant_id = @tenant_id AND entra_object_id = @object_id
              AND is_active = TRUE AND employee_id IS NOT NULL
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("object_id", objectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private async Task<List<string>> GetAvailableIssueTypes(long attendanceDailyId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT late_minutes, missing_minutes, final_status
            FROM public.attendance_daily_records WHERE id=@id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", attendanceDailyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return [];
        var result = new List<string>();
        if (reader.GetInt32(0) > 0 || reader.GetString(2) == "LATE") result.Add("LATE");
        if (reader.GetInt32(1) > 0 || reader.GetString(2) == "ABSENT") result.Add("ABSENT");
        return result;
    }

    private async Task<IReadOnlyList<AttendanceResponseIssueDto>> LoadResponseIssues(
        long responseId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, issue_type, status, reviewed_by, reviewed_by_name, reviewed_at, review_note
            FROM public.attendance_response_issues
            WHERE attendance_response_id=@response_id
            ORDER BY CASE issue_type WHEN 'LATE' THEN 1 ELSE 2 END, id
            """;
        var result = new List<AttendanceResponseIssueDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("response_id", responseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new AttendanceResponseIssueDto(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        return result;
    }

    private async Task<Dictionary<long, IReadOnlyList<AttendanceResponseIssueDto>>> LoadResponseIssues(
        long[] responseIds, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT attendance_response_id, id, issue_type, status, reviewed_by,
                   reviewed_by_name, reviewed_at, review_note
            FROM public.attendance_response_issues
            WHERE attendance_response_id = ANY(@response_ids)
            ORDER BY attendance_response_id,
                     CASE issue_type WHEN 'LATE' THEN 1 ELSE 2 END, id
            """;
        var result = responseIds.ToDictionary(id => id, _ => (IReadOnlyList<AttendanceResponseIssueDto>)[]);
        var grouped = new Dictionary<long, List<AttendanceResponseIssueDto>>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("response_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, responseIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var responseId = reader.GetInt64(0);
            if (!grouped.TryGetValue(responseId, out var values)) grouped[responseId] = values = [];
            values.Add(new AttendanceResponseIssueDto(reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        foreach (var pair in grouped) result[pair.Key] = pair.Value;
        return result;
    }

    private static List<string> NormalizeIssueTypes(IEnumerable<string>? issueTypes) =>
        (issueTypes ?? [])
            .Select(value => value?.Trim().ToUpperInvariant())
            .Where(value => value is "LATE" or "ABSENT")
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string IssueTypeText(string issueType) => issueType switch
    {
        "LATE" => "มาสาย",
        "ABSENT" => "ขาดงาน",
        _ => issueType
    };

    private async Task<IReadOnlyList<AttendanceResponseAttachmentDto>> LoadResponseAttachments(
        long responseId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, original_file_name, content_type, file_size_bytes, uploaded_at
            FROM public.attendance_response_attachments
            WHERE attendance_response_id = @response_id
            ORDER BY uploaded_at, id
            """;
        var result = new List<AttendanceResponseAttachmentDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("response_id", responseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new AttendanceResponseAttachmentDto(reader.GetInt64(0), reader.GetString(1),
                reader.GetString(2), reader.GetInt64(3), reader.GetFieldValue<DateTimeOffset>(4)));
        return result;
    }

    private async Task<Dictionary<long, IReadOnlyList<AttendanceResponseAttachmentDto>>> LoadResponseAttachments(
        long[] responseIds, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT attendance_response_id, id, original_file_name, content_type,
                   file_size_bytes, uploaded_at
            FROM public.attendance_response_attachments
            WHERE attendance_response_id = ANY(@response_ids)
            ORDER BY attendance_response_id, uploaded_at, id
            """;
        var result = responseIds.ToDictionary(id => id, _ => (IReadOnlyList<AttendanceResponseAttachmentDto>)[]);
        var grouped = new Dictionary<long, List<AttendanceResponseAttachmentDto>>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("response_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, responseIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var responseId = reader.GetInt64(0);
            if (!grouped.TryGetValue(responseId, out var values)) grouped[responseId] = values = [];
            values.Add(new AttendanceResponseAttachmentDto(reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetFieldValue<DateTimeOffset>(5)));
        }
        foreach (var pair in grouped) result[pair.Key] = pair.Value;
        return result;
    }

    private static string? ValidateAttachments(IReadOnlyList<AttendanceAttachmentUploadDto>? attachments)
    {
        if (attachments is null || attachments.Count == 0) return null;
        if (attachments.Count > 10) return "แนบไฟล์ได้ไม่เกิน 10 ไฟล์ต่อครั้ง";
        const long maxFileSize = 10 * 1024 * 1024;
        foreach (var attachment in attachments)
        {
            var fileName = Path.GetFileName(attachment.FileName);
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255 ||
                extension is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
                return "รองรับไฟล์แนบเฉพาะ PDF, JPG และ PNG";
            if (attachment.Content is null || attachment.Content.Length == 0 ||
                attachment.Content.LongLength > maxFileSize)
                return $"ไฟล์ {fileName} ต้องมีขนาดมากกว่า 0 และไม่เกิน 10 MB";
            if (attachment.ContentType?.Length > 150)
                return $"Content type ของไฟล์ {fileName} ไม่ถูกต้อง";
        }
        return null;
    }

    private async Task<ActionResult?> ValidateOwnEmployee(
        string employeeId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(employeeId)) return BadRequest("กรุณาระบุรหัสพนักงาน");
        var directEmployeeId = User.FindFirst("employee_id")?.Value;
        if (!string.IsNullOrWhiteSpace(directEmployeeId))
            return string.Equals(directEmployeeId, employeeId, StringComparison.OrdinalIgnoreCase)
                ? null
                : StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์ดูข้อมูลการมาทำงานของพนักงานนี้");

        var tenantId = User.FindFirst("tid")?.Value;
        var objectId = User.FindFirst("oid")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId))
            return Unauthorized();
        const string sql = """
            SELECT employee_id FROM public.microsoft_accounts
            WHERE tenant_id = @tenant_id AND entra_object_id = @object_id AND is_active = TRUE
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("object_id", objectId);
        var authenticatedEmployeeId = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return string.Equals(authenticatedEmployeeId, employeeId, StringComparison.OrdinalIgnoreCase)
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์ดูข้อมูลการมาทำงานของพนักงานนี้");
    }
}
