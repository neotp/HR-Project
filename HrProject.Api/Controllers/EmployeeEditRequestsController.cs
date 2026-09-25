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
        "personalMobile", "homePhone", "internal.macAddress",
        "personal.nationalId", "personal.birthDate", "personal.gender",
        "personal.religion", "personal.bloodType", "personal.residenceProvince",
        "personal.residenceDistrict", "personal.residenceSubdistrict", "personal.residencePostalCode",
        "personal.currentAddress", "personal.idCardAddress", "personal.houseRegistrationAddress",
        "personal.idCardSameAsCurrent", "personal.idCardProvince", "personal.idCardDistrict",
        "personal.idCardSubdistrict", "personal.idCardPostalCode",
        "personal.houseRegistrationSameAsCurrent", "personal.houseRegistrationProvince",
        "personal.houseRegistrationDistrict", "personal.houseRegistrationSubdistrict",
        "personal.houseRegistrationPostalCode",
        "personal.emergencyContactName", "personal.emergencyContactPhone", "personal.emergencyContactAddress",
        "education.history",
        "education.level", "education.institution", "education.major",
        "education.graduationYear",
        "documents.add"
    ];

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EmployeeEditRequestDto>>> GetAll(
        [FromQuery] string? employeeId,
        [FromQuery] string? requestedBy,
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        var requestsOwnData = (!string.IsNullOrWhiteSpace(employeeId) &&
                               string.Equals(employeeId, actor, StringComparison.OrdinalIgnoreCase)) ||
                              (!string.IsNullOrWhiteSpace(requestedBy) &&
                               string.Equals(requestedBy, actor, StringComparison.OrdinalIgnoreCase));
        if (!requestsOwnData &&
            (!await pageAccessService.HasAccess(actor, "EMPLOYEE_EDIT_REQUESTS", cancellationToken) ||
             !await actionPermissionService.HasPermission(actor, "EMPLOYEE_EDIT_REQUESTS", "VIEW_ALL", cancellationToken)))
            return Forbid();

        const string sql = """
            SELECT id, request_no, employee_id, employee_name,
                   changes_json::text, request_reason, status,
                   requested_by_name, requested_at, requested_by,
                   COALESCE((
                       SELECT jsonb_agg(jsonb_build_object(
                           'id', attachment.id,
                           'fileName', attachment.file_name,
                           'contentType', attachment.content_type,
                           'fileSizeBytes', attachment.file_size_bytes,
                           'uploadedAt', attachment.uploaded_at)
                           ORDER BY attachment.uploaded_at, attachment.id)
                       FROM public.employee_edit_request_attachments attachment
                       WHERE attachment.employee_edit_request_id = employee_edit_requests.id
                   ), '[]'::jsonb)::text,
                   (SELECT COUNT(*)::int
                    FROM public.employee_edit_request_comments comment
                    WHERE comment.employee_edit_request_id = employee_edit_requests.id
                      AND comment.is_active = TRUE)
            FROM public.employee_edit_requests
            WHERE (@employee_id IS NULL OR employee_id = @employee_id)
              AND (@requested_by IS NULL OR requested_by = @requested_by)
              AND (@status IS NULL OR status = @status)
            ORDER BY requested_at DESC, id DESC
            """;

        var result = new List<EmployeeEditRequestDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<string?>("employee_id", employeeId));
        command.Parameters.Add(new NpgsqlParameter<string?>("requested_by", requestedBy));
        command.Parameters.Add(new NpgsqlParameter<string?>("status", status));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadRequest(reader));

        return Ok(result);
    }

    [HttpGet("{id:long}/link-destination")]
    public async Task<ActionResult<EmployeeEditRequestLinkDestinationDto>> GetLinkDestination(
        long id, CancellationToken cancellationToken)
    {
        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        var request = await FindById(id, cancellationToken);
        if (request is null)
            return Ok(new EmployeeEditRequestLinkDestinationDto("/employees/my-edit-requests?notice=unavailable"));
        if (string.Equals(request.RequestedBy, actor, StringComparison.OrdinalIgnoreCase))
            return Ok(new EmployeeEditRequestLinkDestinationDto($"/employees/my-edit-requests?requestId={id}"));
        if (await CanReview(actor, cancellationToken))
            return Ok(new EmployeeEditRequestLinkDestinationDto($"/employees/edit-requests?requestId={id}"));
        return Ok(new EmployeeEditRequestLinkDestinationDto("/employees/my-edit-requests?notice=unavailable"));
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<EmployeeEditRequestDto>> GetById(
        long id, CancellationToken cancellationToken)
    {
        var accessError = await ValidateOwnerOrReviewer(id, cancellationToken);
        if (accessError is not null) return accessError;
        var request = await FindById(id, cancellationToken);
        return request is null ? NotFound() : Ok(request);
    }

    [HttpGet("{id:long}/comments")]
    public async Task<ActionResult<IReadOnlyList<EmployeeEditRequestCommentDto>>> GetComments(
        long id, CancellationToken cancellationToken)
    {
        var accessError = await ValidateOwnerOrReviewer(id, cancellationToken);
        if (accessError is not null) return accessError;
        const string sql = """
            SELECT id, employee_edit_request_id, comment_text, commented_by,
                   commented_by_name, commented_at
            FROM public.employee_edit_request_comments
            WHERE employee_edit_request_id=@id AND is_active=TRUE
            ORDER BY commented_at, id
            """;
        var comments = new List<EmployeeEditRequestCommentDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            comments.Add(new EmployeeEditRequestCommentDto(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
        await reader.DisposeAsync();
        for (var index = 0; index < comments.Count; index++)
            comments[index] = comments[index] with
            {
                Attachments = await LoadCommentAttachments(comments[index].Id, cancellationToken)
            };
        return Ok(comments);
    }

    [HttpPost("{id:long}/comments")]
    public async Task<ActionResult<EmployeeEditRequestCommentDto>> AddComment(
        long id, AddEmployeeEditRequestCommentRequest request, CancellationToken cancellationToken)
    {
        var accessError = await ValidateOwnerOrReviewer(id, cancellationToken);
        if (accessError is not null) return accessError;
        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        var text = request.CommentText?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return BadRequest("กรุณากรอกความคิดเห็น");
        if (text.Length > 4000) return BadRequest("ความคิดเห็นต้องไม่เกิน 4,000 ตัวอักษร");
        var attachmentError = ValidateCommentAttachments(request.Attachments);
        if (attachmentError is not null) return BadRequest(attachmentError);
        var actorName = User.FindFirstValue("name") ?? actor;
        const string sql = """
            INSERT INTO public.employee_edit_request_comments
                (employee_edit_request_id, comment_text, commented_by, commented_by_name)
            VALUES (@id, @text, @by, @by_name)
            RETURNING id, commented_at
            """;
        long commentId;
        DateTimeOffset commentedAt;
        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("text", text);
            command.Parameters.AddWithValue("by", actor);
            command.Parameters.AddWithValue("by_name", actorName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            commentId = reader.GetInt64(0);
            commentedAt = reader.GetFieldValue<DateTimeOffset>(1);
        }
        foreach (var attachment in request.Attachments ?? [])
        {
            await using var attachmentCommand = dataSource.CreateCommand("""
                INSERT INTO public.employee_edit_request_comment_attachments
                    (employee_edit_request_comment_id,original_file_name,content_type,file_size_bytes,file_content)
                VALUES (@comment_id,@name,@type,@size,@content)
                """);
            attachmentCommand.Parameters.AddWithValue("comment_id", commentId);
            attachmentCommand.Parameters.AddWithValue("name", Path.GetFileName(attachment.FileName));
            attachmentCommand.Parameters.AddWithValue("type", attachment.ContentType.ToLowerInvariant());
            attachmentCommand.Parameters.AddWithValue("size", (long)attachment.Content.Length);
            attachmentCommand.Parameters.Add("content", NpgsqlDbType.Bytea).Value = attachment.Content;
            await attachmentCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        var editRequest = await FindById(id, cancellationToken);
        try
        {
            if (editRequest is not null && !string.Equals(actor, editRequest.RequestedBy, StringComparison.OrdinalIgnoreCase))
                await workflowNotificationService.SendToEmployeesAsync(actor, [editRequest.RequestedBy],
                    $"มีความคิดเห็นใหม่ในคำขอ {editRequest.RequestNo}", text,
                    $"/open/employee-edit-request/{id}", CancellationToken.None);
            else if (editRequest is not null)
                await workflowNotificationService.SendAsync("EMPLOYEE_EDIT_REQUESTS", actor,
                    $"มีความคิดเห็นใหม่ในคำขอ {editRequest.RequestNo}", text,
                    $"/open/employee-edit-request/{id}", CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Employee edit request comment {CommentId} saved but email failed", commentId);
        }
        return Ok(new EmployeeEditRequestCommentDto(commentId, id, text, actor, actorName, commentedAt)
        { Attachments = await LoadCommentAttachments(commentId, cancellationToken) });
    }

    [HttpGet("{id:long}/comments/{commentId:long}/attachments/{attachmentId:long}/preview")]
    public async Task<IActionResult> PreviewCommentAttachment(
        long id, long commentId, long attachmentId, CancellationToken cancellationToken)
    {
        var accessError = await ValidateOwnerOrReviewer(id, cancellationToken);
        if (accessError is not null) return accessError;
        await using var command = dataSource.CreateCommand("""
            SELECT attachment.content_type,attachment.file_content
            FROM public.employee_edit_request_comment_attachments attachment
            JOIN public.employee_edit_request_comments comment
              ON comment.id=attachment.employee_edit_request_comment_id
            WHERE comment.employee_edit_request_id=@id AND comment.id=@comment_id AND attachment.id=@attachment_id
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("comment_id", commentId);
        command.Parameters.AddWithValue("attachment_id", attachmentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? File((byte[])reader[1], reader.GetString(0))
            : NotFound();
    }

    [HttpGet("{id:long}/attachments/{attachmentId:long}")]
    public async Task<IActionResult> DownloadAttachment(
        long id, long attachmentId, CancellationToken cancellationToken)
    {
        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();

        const string sql = """
            SELECT attachment.file_name, attachment.content_type, attachment.file_content,
                   request.employee_id, request.requested_by
            FROM public.employee_edit_request_attachments attachment
            JOIN public.employee_edit_requests request
              ON request.id = attachment.employee_edit_request_id
            WHERE request.id = @request_id AND attachment.id = @attachment_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("request_id", id);
        command.Parameters.AddWithValue("attachment_id", attachmentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return NotFound();

        var fileName = reader.GetString(0);
        var contentType = reader.GetString(1);
        var content = (byte[])reader[2];
        var employeeId = reader.GetString(3);
        var requestedBy = reader.GetString(4);
        await reader.DisposeAsync();

        var ownsRequest = string.Equals(actor, employeeId, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(actor, requestedBy, StringComparison.OrdinalIgnoreCase);
        if (!ownsRequest &&
            (!await pageAccessService.HasAccess(actor, "EMPLOYEE_EDIT_REQUESTS", cancellationToken) ||
             !await actionPermissionService.HasPermission(actor, "EMPLOYEE_EDIT_REQUESTS", "VIEW_ALL", cancellationToken)))
            return Forbid();

        return File(content, contentType, fileName);
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
        var attachments = request.Attachments?
            .Select(file => file with { FileName = Path.GetFileName(file.FileName ?? string.Empty) })
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

        if (attachments.Count > 5)
            return BadRequest("แนบไฟล์ได้ไม่เกิน 5 ไฟล์ต่อคำขอ");
        foreach (var attachment in attachments)
        {
            var extension = Path.GetExtension(attachment.FileName).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(attachment.FileName) ||
                attachment.FileName.Length > 255 ||
                extension is not (".pdf" or ".jpg" or ".jpeg" or ".png" or ".webp"))
                return BadRequest($"ไฟล์ {attachment.FileName} ไม่รองรับ กรุณาเลือก PDF, JPG, PNG หรือ WEBP");
            if (attachment.Content is null || attachment.Content.Length == 0 || attachment.Content.Length > 3 * 1024 * 1024)
                return BadRequest($"ไฟล์ {attachment.FileName} ต้องมีขนาดไม่เกิน 3 MB");
        }
        if (changes.Any(change => change.FieldKey == "documents.add") && attachments.Count == 0)
            return BadRequest("กรุณาแนบเอกสารส่วนตัวที่ต้องการเพิ่มอย่างน้อย 1 ไฟล์");

        var isOwnRequest = string.Equals(
            actor, request.EmployeeId, StringComparison.OrdinalIgnoreCase);
        if (!isOwnRequest)
        {
            var canRequestForOthers = await actionPermissionService.HasPermission(
                actor, "EMPLOYEES", "REQUEST_EDIT", cancellationToken);
            var requiresEducation = changes.Any(change =>
                change.FieldKey.StartsWith("education.", StringComparison.OrdinalIgnoreCase));
            var requiresDocuments = changes.Any(change =>
                change.FieldKey.StartsWith("documents.", StringComparison.OrdinalIgnoreCase));
            var requiresPersonal = changes.Any(change =>
                !change.FieldKey.StartsWith("education.", StringComparison.OrdinalIgnoreCase) &&
                !change.FieldKey.StartsWith("documents.", StringComparison.OrdinalIgnoreCase));
            var canViewPersonal = !requiresPersonal || await actionPermissionService.HasPermission(
                actor, "EMPLOYEES", "VIEW_PERSONAL", cancellationToken);
            var canViewEducation = !requiresEducation || await actionPermissionService.HasPermission(
                actor, "EMPLOYEES", "VIEW_EDUCATION", cancellationToken);
            var canViewDocuments = !requiresDocuments || await actionPermissionService.HasPermission(
                actor, "EMPLOYEES", "VIEW_PERSONAL_DOCUMENTS", cancellationToken);

            if (!canRequestForOthers || !canViewPersonal || !canViewEducation || !canViewDocuments)
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

        foreach (var attachment in attachments)
        {
            const string attachmentSql = """
                INSERT INTO public.employee_edit_request_attachments
                    (employee_edit_request_id, file_name, content_type, file_size_bytes,
                     file_content, uploaded_by)
                VALUES
                    (@request_id, @file_name, @content_type, @file_size, @content, @uploaded_by)
                """;
            await using var command = new NpgsqlCommand(attachmentSql, connection, transaction);
            command.Parameters.AddWithValue("request_id", id);
            command.Parameters.AddWithValue("file_name", attachment.FileName);
            command.Parameters.AddWithValue("content_type",
                string.IsNullOrWhiteSpace(attachment.ContentType)
                    ? "application/octet-stream"
                    : attachment.ContentType.Trim());
            command.Parameters.AddWithValue("file_size", (long)attachment.Content.Length);
            command.Parameters.Add("content", NpgsqlDbType.Bytea).Value = attachment.Content;
            command.Parameters.AddWithValue("uploaded_by", actor);
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
                $"/open/employee-edit-request/{id}", CancellationToken.None);
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

    [HttpPut("{id:long}")]
    public async Task<ActionResult<EmployeeEditRequestDto>> UpdatePending(
        long id, UpdateEmployeeEditRequest request, CancellationToken cancellationToken)
    {
        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        var existing = await FindById(id, cancellationToken);
        if (existing is null) return NotFound();
        if (!string.Equals(existing.RequestedBy, actor, StringComparison.OrdinalIgnoreCase)) return Forbid();
        if (existing.Status != "PENDING") return Conflict("แก้ไขได้เฉพาะคำขอที่รออนุมัติ");

        var existingChanges = existing.Changes.ToDictionary(
            change => change.FieldKey, StringComparer.OrdinalIgnoreCase);
        var changes = request.Changes?
            .Where(change => AllowedFields.Contains(change.FieldKey))
            .GroupBy(change => change.FieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var submitted = group.First();
                return existingChanges.TryGetValue(submitted.FieldKey, out var original)
                    ? original with { NewValue = submitted.NewValue }
                    : submitted;
            })
            .Where(change => !string.Equals(change.OldValue, change.NewValue, StringComparison.Ordinal))
            .ToList() ?? [];
        if (changes.Count == 0 || string.IsNullOrWhiteSpace(request.RequestReason))
            return BadRequest("กรุณาระบุข้อมูลที่ต้องการแก้ไขและเหตุผล");
        var updatedProfileImage = changes.FirstOrDefault(change =>
            string.Equals(change.FieldKey, "profileImage", StringComparison.OrdinalIgnoreCase));
        if (updatedProfileImage is not null && !IsValidProfileImage(updatedProfileImage.NewValue))
            return BadRequest("รูปโปรไฟล์ไม่ถูกต้องหรือมีขนาดใหญ่เกิน 2 MB");
        var updatedEmployeeStatus = changes.FirstOrDefault(change =>
            string.Equals(change.FieldKey, "internal.employeeStatus", StringComparison.OrdinalIgnoreCase));
        if (updatedEmployeeStatus is not null && !EmployeeStatusValues.IsValid(updatedEmployeeStatus.NewValue))
            return BadRequest("สถานะพนักงานไม่ถูกต้อง");
        var additions = request.NewAttachments?.ToList() ?? [];
        var existingAttachmentIds = existing.Attachments.Select(item => item.Id).ToHashSet();
        var removedAttachmentIds = (request.RemovedAttachmentIds ?? [])
            .Where(existingAttachmentIds.Contains).Distinct().ToArray();
        if (changes.Any(change => change.FieldKey == "documents.add") &&
            existing.Attachments.Count - removedAttachmentIds.Length + additions.Count == 0)
            return BadRequest("กรุณาแนบเอกสารส่วนตัวที่ต้องการเพิ่มอย่างน้อย 1 ไฟล์");
        if (existing.Attachments.Count - removedAttachmentIds.Length + additions.Count > 5)
            return BadRequest("แนบไฟล์ได้ไม่เกิน 5 ไฟล์ต่อคำขอ");
        foreach (var attachment in additions)
        {
            var extension = Path.GetExtension(attachment.FileName).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(attachment.FileName) ||
                extension is not (".pdf" or ".jpg" or ".jpeg" or ".png" or ".webp") ||
                attachment.Content is null || attachment.Content.Length == 0 || attachment.Content.Length > 3 * 1024 * 1024)
                return BadRequest("ไฟล์แนบไม่ถูกต้อง รองรับ PDF, JPG, PNG และ WEBP ขนาดไม่เกิน 3 MB");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("""
            UPDATE public.employee_edit_requests
            SET changes_json=@changes, request_reason=@reason
            WHERE id=@id AND status='PENDING' AND requested_by=@actor
            """, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter("changes", NpgsqlDbType.Jsonb)
                { Value = JsonSerializer.Serialize(changes) });
            command.Parameters.AddWithValue("reason", request.RequestReason.Trim());
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", actor);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                return Conflict("คำขอไม่ได้อยู่ในสถานะที่แก้ไขได้");
        }
        if (removedAttachmentIds.Length > 0)
        {
            await using var command = new NpgsqlCommand("""
                DELETE FROM public.employee_edit_request_attachments
                WHERE employee_edit_request_id=@id AND id=ANY(@attachment_ids)
                """, connection, transaction);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("attachment_ids", removedAttachmentIds);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var attachment in additions)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO public.employee_edit_request_attachments
                    (employee_edit_request_id,file_name,content_type,file_size_bytes,file_content,uploaded_by)
                VALUES (@id,@name,@type,@size,@content,@actor)
                """, connection, transaction);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("name", Path.GetFileName(attachment.FileName));
            command.Parameters.AddWithValue("type", string.IsNullOrWhiteSpace(attachment.ContentType) ? "application/octet-stream" : attachment.ContentType);
            command.Parameters.AddWithValue("size", (long)attachment.Content.Length);
            command.Parameters.Add("content", NpgsqlDbType.Bytea).Value = attachment.Content;
            command.Parameters.AddWithValue("actor", actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = new NpgsqlCommand("""
            INSERT INTO public.employee_edit_request_history
                (employee_edit_request_id,action,details_text,action_by,action_by_name)
            VALUES (@id,'UPDATE_REQUEST','แก้ไขคำขอระหว่างรออนุมัติ',@actor,@name)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("name", User.FindFirstValue("name") ?? actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return Ok(await FindById(id, cancellationToken));
    }

    [HttpPut("{id:long}/cancel")]
    public async Task<ActionResult<EmployeeEditRequestDto>> CancelPending(
        long id, CancellationToken cancellationToken)
    {
        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("""
            UPDATE public.employee_edit_requests SET status='CANCELLED'
            WHERE id=@id AND status='PENDING' AND requested_by=@actor
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", actor);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Conflict("ยกเลิกได้เฉพาะคำขอของตนเองที่ยังรออนุมัติ");
            }
        }
        await using (var command = new NpgsqlCommand("""
            INSERT INTO public.employee_edit_request_history
                (employee_edit_request_id,action,details_text,action_by,action_by_name)
            VALUES (@id,'CANCEL_REQUEST','ยกเลิกคำขอระหว่างรออนุมัติ',@actor,@name)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("name", User.FindFirstValue("name") ?? actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        var result = await FindById(id, cancellationToken);
        if (result is null)
            return Conflict("ยกเลิกได้เฉพาะคำขอของตนเองที่ยังรออนุมัติ");
        return Ok(result);
    }

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
        if (string.Equals(actor, pendingRequest.RequestedBy, StringComparison.OrdinalIgnoreCase))
            return StatusCode(
                StatusCodes.Status403Forbidden,
                "ไม่สามารถอนุมัติหรือไม่อนุมัติคำขอแก้ไขข้อมูลพนักงานที่ตนเองเป็นผู้ยื่นได้");

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

            await CopyApprovedAttachmentsToPersonalDocuments(
                connection, transaction, id, employeeId.Value,
                pendingRequest.RequestNo, pendingRequest.RequestedBy,
                pendingRequest.RequestedByName, cancellationToken);

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

    private static async Task CopyApprovedAttachmentsToPersonalDocuments(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long requestId,
        long employeeId,
        string requestNo,
        string requestedBy,
        string requestedByName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH inserted AS
            (
                INSERT INTO public.employee_personal_documents
                    (employee_id, original_file_name, content_type, file_size_bytes,
                     file_content, uploaded_by, uploaded_by_name,
                     source_type, source_id, source_attachment_id)
                SELECT @employee_id, attachment.file_name,
                       LEFT(attachment.content_type, 100), attachment.file_size_bytes,
                       attachment.file_content, @uploaded_by, @uploaded_by_name,
                       'EMPLOYEE_EDIT_REQUEST', @request_id, attachment.id
                FROM public.employee_edit_request_attachments attachment
                WHERE attachment.employee_edit_request_id = @request_id
                ON CONFLICT (source_type, source_id, source_attachment_id)
                    WHERE source_type IS NOT NULL
                      AND source_id IS NOT NULL
                      AND source_attachment_id IS NOT NULL
                DO NOTHING
                RETURNING id, original_file_name, file_size_bytes
            )
            INSERT INTO public.employee_activity_history
                (employee_id, action_key, details_text, entity_type, entity_id,
                 action_by, action_by_name)
            SELECT @employee_id, 'PERSONAL_DOCUMENT_ADDED',
                   CONCAT('เพิ่มเอกสาร ', original_file_name, ' จากคำขอแก้ไข ',
                          @request_no, ' ขนาด ', file_size_bytes, ' ไบต์'),
                   'PERSONAL_DOCUMENT', id, @uploaded_by, @uploaded_by_name
            FROM inserted
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("employee_id", employeeId);
        command.Parameters.AddWithValue("request_id", requestId);
        command.Parameters.AddWithValue("request_no", requestNo);
        command.Parameters.AddWithValue("uploaded_by", requestedBy);
        command.Parameters.AddWithValue("uploaded_by_name", requestedByName);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
                   requested_by_name, requested_at, requested_by,
                   COALESCE((
                       SELECT jsonb_agg(jsonb_build_object(
                           'id', attachment.id,
                           'fileName', attachment.file_name,
                           'contentType', attachment.content_type,
                           'fileSizeBytes', attachment.file_size_bytes,
                           'uploadedAt', attachment.uploaded_at)
                           ORDER BY attachment.uploaded_at, attachment.id)
                       FROM public.employee_edit_request_attachments attachment
                       WHERE attachment.employee_edit_request_id = employee_edit_requests.id
                   ), '[]'::jsonb)::text,
                   (SELECT COUNT(*)::int
                    FROM public.employee_edit_request_comments comment
                    WHERE comment.employee_edit_request_id = employee_edit_requests.id
                      AND comment.is_active = TRUE)
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
        var attachments = JsonSerializer.Deserialize<List<EmployeeEditRequestAttachmentDto>>(
            reader.GetString(10),
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
            reader.GetFieldValue<DateTimeOffset>(8))
        {
            RequestedBy = reader.GetString(9),
            Attachments = attachments,
            CommentCount = reader.GetInt32(11)
        };
    }

    private async Task<bool> CanReview(string employeeId, CancellationToken cancellationToken) =>
        await pageAccessService.HasAccess(employeeId, "EMPLOYEE_EDIT_REQUESTS", cancellationToken) &&
        (await actionPermissionService.HasPermission(employeeId, "EMPLOYEE_EDIT_REQUESTS", "APPROVE", cancellationToken) ||
         await actionPermissionService.HasPermission(employeeId, "EMPLOYEE_EDIT_REQUESTS", "REJECT", cancellationToken));

    private async Task<IReadOnlyList<LeaveCommentAttachmentDto>> LoadCommentAttachments(
        long commentId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id,original_file_name,content_type,file_size_bytes,uploaded_at
            FROM public.employee_edit_request_comment_attachments
            WHERE employee_edit_request_comment_id=@comment_id
            ORDER BY uploaded_at,id
            """;
        var result = new List<LeaveCommentAttachmentDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("comment_id", commentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new LeaveCommentAttachmentDto(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),reader.GetInt64(3),reader.GetFieldValue<DateTimeOffset>(4)));
        return result;
    }

    private static string? ValidateCommentAttachments(IReadOnlyList<LeaveCommentAttachmentUploadDto>? attachments)
    {
        if ((attachments?.Count ?? 0) > 5) return "แนบรูปได้ไม่เกิน 5 รูปต่อ Comment";
        foreach (var attachment in attachments ?? [])
            if (string.IsNullOrWhiteSpace(attachment.FileName) ||
                attachment.ContentType.ToLowerInvariant() is not ("image/png" or "image/jpeg" or "image/webp") ||
                attachment.Content is null || attachment.Content.Length is < 1 or > 3145728)
                return "รองรับเฉพาะ PNG, JPG และ WEBP ขนาดไม่เกิน 3 MB";
        return null;
    }

    private async Task<ActionResult?> ValidateOwnerOrReviewer(
        long id, CancellationToken cancellationToken)
    {
        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        var request = await FindById(id, cancellationToken);
        if (request is null) return NotFound();
        if (string.Equals(request.RequestedBy, actor, StringComparison.OrdinalIgnoreCase)) return null;
        return await CanReview(actor, cancellationToken)
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์เข้าถึงคำขอนี้");
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
