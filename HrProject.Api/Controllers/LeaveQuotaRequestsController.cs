using HrProject.Shared.Models;
using HrProject.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/leave-quota-requests")]
public sealed class LeaveQuotaRequestsController(
    NpgsqlDataSource dataSource,
    PageActionPermissionService actionPermissionService) : ControllerBase
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
                   r.requested_at
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
                reader.GetFieldValue<DateTimeOffset>(11)));
        }

        return Ok(result);
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
        var created = new List<LeaveQuotaRequestDto>(createdIds.Count);
        foreach (var id in createdIds)
        {
            var item = await FindById(id, cancellationToken);
            if (item is not null)
                created.Add(item);
        }

        return StatusCode(StatusCodes.Status201Created, created);
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

        var actionKey = approve ? "APPROVE" : "REJECT";
        var hasPermission = await actionPermissionService.HasPermission(
            request.ReviewedBy, "LEAVE_REQUEST_QUOTA", actionKey, cancellationToken);
        if (!hasPermission)
        {
            // VIEW_ALL was the original administrator permission for this page.
            // Keep it as a compatibility fallback for users configured before
            // APPROVE and REJECT were introduced as separate actions.
            hasPermission = await actionPermissionService.HasPermission(
                request.ReviewedBy, "LEAVE_REQUEST_QUOTA", "VIEW_ALL", cancellationToken);
        }
        if (!hasPermission)
            return StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์ดำเนินการคำขอเพิ่มโควต้า");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string selectSql = """
            SELECT employee_id, leave_type_id, quota_year, requested_hours, status
            FROM public.leave_quota_requests
            WHERE id = @id
            FOR UPDATE
            """;
        string employeeId;
        long leaveTypeId;
        short quotaYear;
        decimal requestedHours;
        string status;
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
        }

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
            command.Parameters.AddWithValue("reviewed_by", request.ReviewedBy.Trim());
            command.Parameters.AddWithValue("reviewed_by_name", request.ReviewedByName.Trim());
            command.Parameters.Add(new NpgsqlParameter<string?>("remark", request.Remark?.Trim()));
            command.Parameters.AddWithValue("id", id);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                return Conflict("คำขอนี้ถูกดำเนินการไปแล้ว");
        }

        if (approve)
        {
            const string upsertQuotaSql = """
                INSERT INTO public.leave_quotas
                    (employee_id, leave_type_id, quota_year, quota_hours, notes,
                     created_by, created_by_name, updated_by, updated_by_name)
                VALUES
                    (@employee_id, @leave_type_id, @quota_year, @hours, @notes,
                     @action_by, @action_by_name, @action_by, @action_by_name)
                ON CONFLICT (employee_id, leave_type_id, quota_year)
                DO UPDATE SET
                    quota_hours = public.leave_quotas.quota_hours + EXCLUDED.quota_hours,
                    notes = EXCLUDED.notes,
                    updated_by = EXCLUDED.updated_by,
                    updated_by_name = EXCLUDED.updated_by_name
                RETURNING id, quota_hours
                """;
            long quotaId;
            decimal totalQuotaHours;
            await using (var command = new NpgsqlCommand(upsertQuotaSql, connection, transaction))
            {
                command.Parameters.AddWithValue("employee_id", employeeId);
                command.Parameters.AddWithValue("leave_type_id", leaveTypeId);
                command.Parameters.AddWithValue("quota_year", quotaYear);
                command.Parameters.AddWithValue("hours", approvedHours!.Value);
                command.Parameters.AddWithValue("notes", $"เพิ่มจากคำขอ {id}");
                command.Parameters.AddWithValue("action_by", request.ReviewedBy.Trim());
                command.Parameters.AddWithValue("action_by_name", request.ReviewedByName.Trim());
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                quotaId = reader.GetInt64(0);
                totalQuotaHours = reader.GetDecimal(1);
            }

            const string quotaHistorySql = """
                INSERT INTO public.leave_quota_history
                    (leave_quota_id, action, details_text, after_data,
                     action_by, action_by_name)
                VALUES
                    (@quota_id, 'UPDATE', @details, CAST(@after_data AS jsonb),
                     @action_by, @action_by_name)
                """;
            await using var historyCommand = new NpgsqlCommand(quotaHistorySql, connection, transaction);
            historyCommand.Parameters.AddWithValue("quota_id", quotaId);
            historyCommand.Parameters.AddWithValue(
                "details",
                $"อนุมัติคำขอเพิ่มโควต้า {approvedHours.Value:0.##} ชั่วโมง; โควต้ารวม {totalQuotaHours:0.##} ชั่วโมง");
            historyCommand.Parameters.AddWithValue(
                "after_data",
                $"{{\"quotaHours\":{totalQuotaHours.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
            historyCommand.Parameters.AddWithValue("action_by", request.ReviewedBy.Trim());
            historyCommand.Parameters.AddWithValue("action_by_name", request.ReviewedByName.Trim());
            await historyCommand.ExecuteNonQueryAsync(cancellationToken);
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
            command.Parameters.AddWithValue("action_by", request.ReviewedBy.Trim());
            command.Parameters.AddWithValue("action_by_name", request.ReviewedByName.Trim());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    private async Task<LeaveQuotaRequestDto?> FindById(long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT r.id, r.request_no, r.employee_id, r.leave_type_id,
                   t.name_th, r.quota_year, r.requested_hours,
                   r.approved_hours, r.request_reason, r.status, r.requested_by_name,
                   r.requested_at
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
            reader.GetString(10), reader.GetFieldValue<DateTimeOffset>(11));
    }
}
