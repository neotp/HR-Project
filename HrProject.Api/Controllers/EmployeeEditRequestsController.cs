using System.Text.Json;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/employee-edit-requests")]
public sealed class EmployeeEditRequestsController(NpgsqlDataSource dataSource) : ControllerBase
{
    private static readonly HashSet<string> AllowedFields =
    [
        "title", "firstName", "lastName", "thaiFullName", "englishFullName", "email",
        "personal.nationalId", "personal.birthDate", "personal.gender",
        "personal.religion", "personal.bloodType", "personal.residenceProvince",
        "personal.idCardAddress", "personal.houseRegistrationAddress",
        "work.history",
        "education.history",
        "education.level", "education.institution", "education.major",
        "education.graduationYear",
        "family.maritalStatus", "family.spouseName", "family.spouseNationalId"
    ];

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EmployeeEditRequestDto>>> GetAll(
        [FromQuery] string? employeeId,
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
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
            changes.Any(change =>
                string.IsNullOrWhiteSpace(change.FieldName) ||
                string.IsNullOrWhiteSpace(change.NewValue)) ||
            string.IsNullOrWhiteSpace(request.RequestReason) ||
            string.IsNullOrWhiteSpace(request.RequestedBy) ||
            string.IsNullOrWhiteSpace(request.RequestedByName))
        {
            return BadRequest("กรุณาระบุข้อมูลที่ต้องการแก้ไขและเหตุผลให้ครบถ้วน");
        }

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

        var details = string.Join("; ", changes.Select(change =>
            $"{change.FieldName}: {change.OldValue} → {change.NewValue}"));
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
        var created = await FindById(id, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, created);
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

        await transaction.CommitAsync(cancellationToken);

        if (newStatus == "APPROVED" &&
            !await EmployeesController.ApplyApprovedChanges(
                dataSource,
                pendingRequest.EmployeeId,
                pendingRequest.Changes,
                cancellationToken))
        {
            return Conflict("อนุมัติเอกสารแล้ว แต่ไม่พบข้อมูลพนักงานสำหรับปรับปรุง");
        }

        return Ok(await FindById(id, cancellationToken));
    }

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
}
