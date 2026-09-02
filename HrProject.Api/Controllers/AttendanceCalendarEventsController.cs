using HrProject.Shared.Models;
using HrProject.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/attendance-calendar-events")]
public sealed class AttendanceCalendarEventsController(
    NpgsqlDataSource dataSource,
    PageAccessService pageAccessService,
    PageActionPermissionService actionPermissionService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AttendanceCalendarEventDto>>> GetAll(
        [FromQuery] DateOnly? startDate,
        [FromQuery] DateOnly? endDate,
        CancellationToken cancellationToken)
    {
        var actor = await GetAuthenticatedEmployee(cancellationToken);
        if (actor is null) return Unauthorized();
        var from = startDate ?? new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        var to = endDate ?? from.AddMonths(1).AddDays(-1);
        if (to < from || to.DayNumber - from.DayNumber > 366)
            return BadRequest("ช่วงวันที่ต้องไม่เกิน 366 วัน");

        const string sql = """
            SELECT id, employee_id, event_date, event_type, start_time, end_time,
                   title, details, status, created_by, created_by_name, created_at, updated_at,
                   reviewed_by, reviewed_by_name, reviewed_at, review_note
            FROM public.attendance_calendar_events
            WHERE employee_id = @employee_id AND event_date BETWEEN @start_date AND @end_date
            ORDER BY event_date, start_time, id
            """;
        var result = new List<AttendanceCalendarEventDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", actor.Value.EmployeeId);
        command.Parameters.AddWithValue("start_date", from);
        command.Parameters.AddWithValue("end_date", to);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadEvent(reader));
        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<AttendanceCalendarEventDto>> Create(
        SaveAttendanceCalendarEventRequest request, CancellationToken cancellationToken)
    {
        var actor = await GetAuthenticatedEmployee(cancellationToken);
        if (actor is null) return Unauthorized();
        var validationError = Validate(request);
        if (validationError is not null) return BadRequest(validationError);
        if (!await IsEventTypeAllowed(request.EventType, null, cancellationToken))
            return BadRequest("ประเภท Event ไม่ถูกต้องหรือปิดใช้งานแล้ว");

        var status = await RequiresPastEventReview(request, cancellationToken)
            ? "PENDING_REVIEW"
            : "APPROVED";
        const string sql = """
            INSERT INTO public.attendance_calendar_events
                (employee_id, event_date, event_type, start_time, end_time, title, details,
                 status, created_by, created_by_name)
            VALUES
                (@employee_id, @event_date, @event_type, @start_time, @end_time, @title, @details,
                 @status, @created_by, @created_by_name)
            RETURNING id, employee_id, event_date, event_type, start_time, end_time,
                      title, details, status, created_by, created_by_name, created_at, updated_at,
                      reviewed_by, reviewed_by_name, reviewed_at, review_note
            """;
        await using var command = dataSource.CreateCommand(sql);
        AddParameters(command, actor.Value.EmployeeId, request);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("created_by", actor.Value.EmployeeId);
        command.Parameters.AddWithValue("created_by_name", actor.Value.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var result = ReadEvent(reader);
        return Created($"api/attendance-calendar-events/{result.Id}", result);
    }

    [HttpPut("{id:long}")]
    public async Task<ActionResult<AttendanceCalendarEventDto>> Update(
        long id, SaveAttendanceCalendarEventRequest request, CancellationToken cancellationToken)
    {
        var actor = await GetAuthenticatedEmployee(cancellationToken);
        if (actor is null) return Unauthorized();
        var validationError = Validate(request);
        if (validationError is not null) return BadRequest(validationError);
        var currentEventType = await GetOwnedEventType(id, actor.Value.EmployeeId, cancellationToken);
        if (currentEventType is null) return NotFound();
        if (!await IsEventTypeAllowed(request.EventType, currentEventType, cancellationToken))
            return BadRequest("ประเภท Event ไม่ถูกต้องหรือปิดใช้งานแล้ว");

        var status = await RequiresPastEventReview(request, cancellationToken)
            ? "PENDING_REVIEW"
            : "APPROVED";
        const string sql = """
            UPDATE public.attendance_calendar_events
            SET event_date = @event_date, event_type = @event_type,
                start_time = @start_time, end_time = @end_time,
                title = @title, details = @details, status = @status,
                reviewed_by = NULL, reviewed_by_name = NULL, reviewed_at = NULL, review_note = NULL,
                updated_at = CURRENT_TIMESTAMP
            WHERE id = @id AND employee_id = @employee_id
            RETURNING id, employee_id, event_date, event_type, start_time, end_time,
                      title, details, status, created_by, created_by_name, created_at, updated_at,
                      reviewed_by, reviewed_by_name, reviewed_at, review_note
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        AddParameters(command, actor.Value.EmployeeId, request);
        command.Parameters.AddWithValue("status", status);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Ok(ReadEvent(reader)) : NotFound();
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var actor = await GetAuthenticatedEmployee(cancellationToken);
        if (actor is null) return Unauthorized();
        const string sql = "DELETE FROM public.attendance_calendar_events WHERE id = @id AND employee_id = @employee_id";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("employee_id", actor.Value.EmployeeId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 0 ? NotFound() : NoContent();
    }

    [HttpGet("reviews")]
    public async Task<ActionResult<IReadOnlyList<AttendanceCalendarEventReviewDto>>> GetReviews(
        [FromQuery] DateOnly? startDate,
        [FromQuery] DateOnly? endDate,
        CancellationToken cancellationToken)
    {
        var actor = await GetAuthenticatedEmployee(cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await pageAccessService.HasAccess(actor.Value.EmployeeId, "ATTENDANCE_REVIEWS", cancellationToken))
            return Forbid();

        var from = startDate ?? new DateOnly(DateTime.Today.Year, 1, 1);
        var to = endDate ?? new DateOnly(DateTime.Today.Year, 12, 31);
        if (to < from || to.DayNumber - from.DayNumber > 366) return BadRequest("Invalid date range");

        const string sql = """
            SELECT event.id, event.employee_id,
                   COALESCE(NULLIF(basic.full_name_th, ''), NULLIF(basic.full_name_en, ''), event.employee_id),
                   COALESCE(company.department, ''), event.event_date, event.event_type,
                   event_type.name_th, event.start_time, event.end_time, event.title, event.details,
                   event.status, event.created_by, event.created_by_name, event.created_at,
                   event.reviewed_by, event.reviewed_by_name, event.reviewed_at, event.review_note
            FROM public.attendance_calendar_events event
            JOIN public.attendance_event_types event_type ON event_type.code = event.event_type
            JOIN public.employees employee ON employee.employee_code = event.employee_id
            LEFT JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
            LEFT JOIN public.employee_company_info company ON company.employee_id = employee.id
            WHERE event.event_date BETWEEN @start_date AND @end_date
              AND event.status = 'PENDING_REVIEW'
            ORDER BY event.created_at, event.id
            """;
        var result = new List<AttendanceCalendarEventReviewDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("start_date", from);
        command.Parameters.AddWithValue("end_date", to);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new AttendanceCalendarEventReviewDto(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<DateOnly>(4), reader.GetString(5), reader.GetString(6),
                reader.GetFieldValue<TimeOnly>(7), reader.GetFieldValue<TimeOnly>(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13), reader.GetFieldValue<DateTimeOffset>(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
                reader.IsDBNull(18) ? null : reader.GetString(18)));
        return Ok(result);
    }

    [HttpPost("reviews/{id:long}/decision")]
    public async Task<IActionResult> Review(
        long id, ReviewAttendanceCalendarEventRequest request, CancellationToken cancellationToken)
    {
        var decision = request.Decision?.Trim().ToUpperInvariant();
        if (decision is not ("APPROVE" or "REJECT")) return BadRequest("Invalid decision");
        if (request.ReviewNote?.Length > 2000) return BadRequest("Review note is too long");
        var actor = await GetAuthenticatedEmployee(cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await pageAccessService.HasAccess(actor.Value.EmployeeId, "ATTENDANCE_REVIEWS", cancellationToken) ||
            !await actionPermissionService.HasPermission(actor.Value.EmployeeId, "ATTENDANCE_REVIEWS", decision, cancellationToken))
            return Forbid();

        const string sql = """
            UPDATE public.attendance_calendar_events
            SET status = @status, reviewed_by = @reviewed_by, reviewed_by_name = @reviewed_by_name,
                reviewed_at = CURRENT_TIMESTAMP, review_note = @review_note, updated_at = CURRENT_TIMESTAMP
            WHERE id = @id AND status = 'PENDING_REVIEW'
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", decision == "APPROVE" ? "APPROVED" : "REJECTED");
        command.Parameters.AddWithValue("reviewed_by", actor.Value.EmployeeId);
        command.Parameters.AddWithValue("reviewed_by_name", actor.Value.Name);
        command.Parameters.AddWithValue("review_note", (object?)NullIfWhiteSpace(request.ReviewNote) ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 0 ? Conflict("Event is no longer pending review") : NoContent();
    }

    private static string? Validate(SaveAttendanceCalendarEventRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EventType)) return "กรุณาเลือกประเภท Event";
        if (request.EndTime <= request.StartTime) return "เวลาสิ้นสุดต้องมากกว่าเวลาเริ่มต้น";
        if (request.Title?.Trim().Length > 200) return "หัวข้อยาวได้ไม่เกิน 200 ตัวอักษร";
        if (request.Details?.Trim().Length > 2000) return "รายละเอียดลงได้ไม่เกิน 2,000 ตัวอักษร";
        return null;
    }

    private async Task<bool> IsEventTypeAllowed(
        string eventType, string? currentEventType, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS
            (
                SELECT 1
                FROM public.attendance_event_types
                WHERE code = @code
                  AND (is_active = TRUE OR code = @current_code)
            )
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("code", eventType.Trim().ToUpperInvariant());
        command.Parameters.Add(new NpgsqlParameter<string?>("current_code", currentEventType));
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<bool> RequiresPastEventReview(
        SaveAttendanceCalendarEventRequest request, CancellationToken cancellationToken)
    {
        if (request.EventDate >= DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7))) return false;
        const string sql = "SELECT counts_as_work_time FROM public.attendance_event_types WHERE code = @code";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("code", request.EventType.Trim().ToUpperInvariant());
        return (bool?)await command.ExecuteScalarAsync(cancellationToken) ?? false;
    }

    private async Task<string?> GetOwnedEventType(
        long id, string employeeId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT event_type
            FROM public.attendance_calendar_events
            WHERE id = @id AND employee_id = @employee_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("employee_id", employeeId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static void AddParameters(NpgsqlCommand command, string employeeId,
        SaveAttendanceCalendarEventRequest request)
    {
        command.Parameters.AddWithValue("employee_id", employeeId);
        command.Parameters.AddWithValue("event_date", request.EventDate);
        command.Parameters.AddWithValue("event_type", request.EventType.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("start_time", request.StartTime);
        command.Parameters.AddWithValue("end_time", request.EndTime);
        command.Parameters.AddWithValue("title", (object?)NullIfWhiteSpace(request.Title) ?? DBNull.Value);
        command.Parameters.AddWithValue("details", (object?)NullIfWhiteSpace(request.Details) ?? DBNull.Value);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AttendanceCalendarEventDto ReadEvent(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetFieldValue<DateOnly>(2), reader.GetString(3),
        reader.GetFieldValue<TimeOnly>(4), reader.GetFieldValue<TimeOnly>(5),
        reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetFieldValue<DateTimeOffset>(11),
        reader.GetFieldValue<DateTimeOffset>(12), reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
        reader.IsDBNull(16) ? null : reader.GetString(16));

    private async Task<(string EmployeeId, string Name)?> GetAuthenticatedEmployee(CancellationToken cancellationToken)
    {
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
        return await reader.ReadAsync(cancellationToken) ? (reader.GetString(0), reader.GetString(1)) : null;
    }
}
