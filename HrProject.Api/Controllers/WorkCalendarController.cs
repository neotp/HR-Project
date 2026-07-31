using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/work-calendar")]
public sealed class WorkCalendarController(NpgsqlDataSource dataSource) : ControllerBase
{
    private static readonly string[] ValidDayTypes = ["PUBLIC_HOLIDAY", "WORKING_SATURDAY"];

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkCalendarDayDto>>> GetAll(
        [FromQuery] int? year,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, calendar_date, day_type, name, note,
                   updated_by, updated_by_name, updated_at
            FROM public.work_calendar_days
            WHERE (@year IS NULL OR EXTRACT(YEAR FROM calendar_date) = @year)
            ORDER BY calendar_date, id
            """;

        var result = new List<WorkCalendarDayDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<int?>("year", year));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadCalendarDay(reader));

        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<WorkCalendarDayDto>> Save(
        SaveWorkCalendarDayRequest request,
        CancellationToken cancellationToken)
    {
        var dayType = request.DayType.Trim().ToUpperInvariant();
        var validationError = Validate(
            request.CalendarDate,
            dayType,
            request.Name,
            request.ActionBy,
            request.ActionByName);
        if (validationError is not null)
            return BadRequest(validationError);

        const string sql = """
            INSERT INTO public.work_calendar_days
                (calendar_date, day_type, name, note,
                 created_by, created_by_name, updated_by, updated_by_name)
            VALUES
                (@calendar_date, @day_type, @name, @note,
                 @action_by, @action_by_name, @action_by, @action_by_name)
            ON CONFLICT (calendar_date)
            DO UPDATE SET
                day_type = EXCLUDED.day_type,
                name = EXCLUDED.name,
                note = EXCLUDED.note,
                updated_by = EXCLUDED.updated_by,
                updated_by_name = EXCLUDED.updated_by_name,
                updated_at = CURRENT_TIMESTAMP
            RETURNING id
            """;

        long id;
        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("calendar_date", request.CalendarDate);
            command.Parameters.AddWithValue("day_type", dayType);
            command.Parameters.AddWithValue("name", request.Name.Trim());
            command.Parameters.Add(new NpgsqlParameter<string?>(
                "note",
                string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()));
            command.Parameters.AddWithValue("action_by", request.ActionBy.Trim());
            command.Parameters.AddWithValue("action_by_name", request.ActionByName.Trim());
            id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        var saved = await FindById(id, cancellationToken);
        return Ok(saved);
    }

    [HttpPost("batch")]
    public async Task<ActionResult> SaveBatch(
        SaveWorkCalendarDaysBatchRequest request,
        CancellationToken cancellationToken)
    {
        var dayType = request.DayType.Trim().ToUpperInvariant();
        if (request.Items.Count == 0)
            return BadRequest("กรุณาเลือกอย่างน้อย 1 วัน");
        if (request.Items.Count > 366)
            return BadRequest("บันทึกได้ครั้งละไม่เกิน 366 วัน");
        if (request.Items.Select(item => item.CalendarDate).Distinct().Count() != request.Items.Count)
            return BadRequest("พบวันที่ซ้ำในรายการที่เลือก");

        foreach (var item in request.Items)
        {
            var validationError = Validate(
                item.CalendarDate,
                dayType,
                item.Name,
                request.ActionBy,
                request.ActionByName);
            if (validationError is not null)
                return BadRequest($"{item.CalendarDate:dd/MM/yyyy}: {validationError}");
        }

        const string sql = """
            INSERT INTO public.work_calendar_days
                (calendar_date, day_type, name, note,
                 created_by, created_by_name, updated_by, updated_by_name)
            VALUES
                (@calendar_date, @day_type, @name, @note,
                 @action_by, @action_by_name, @action_by, @action_by_name)
            ON CONFLICT (calendar_date)
            DO UPDATE SET
                day_type = EXCLUDED.day_type,
                name = EXCLUDED.name,
                note = EXCLUDED.note,
                updated_by = EXCLUDED.updated_by,
                updated_by_name = EXCLUDED.updated_by_name,
                updated_at = CURRENT_TIMESTAMP
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var item in request.Items)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("calendar_date", item.CalendarDate);
            command.Parameters.AddWithValue("day_type", dayType);
            command.Parameters.AddWithValue("name", item.Name.Trim());
            command.Parameters.Add(new NpgsqlParameter<string?>(
                "note",
                string.IsNullOrWhiteSpace(item.Note) ? null : item.Note.Trim()));
            command.Parameters.AddWithValue("action_by", request.ActionBy.Trim());
            command.Parameters.AddWithValue("action_by_name", request.ActionByName.Trim());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return Ok(new { count = request.Items.Count });
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        const string sql = "DELETE FROM public.work_calendar_days WHERE id = @id";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        return affectedRows == 0 ? NotFound() : NoContent();
    }

    private async Task<WorkCalendarDayDto?> FindById(long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, calendar_date, day_type, name, note,
                   updated_by, updated_by_name, updated_at
            FROM public.work_calendar_days
            WHERE id = @id
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCalendarDay(reader) : null;
    }

    private static string? Validate(
        DateOnly calendarDate,
        string dayType,
        string name,
        string actionBy,
        string actionByName)
    {
        if (!ValidDayTypes.Contains(dayType, StringComparer.Ordinal))
            return "ประเภทวันในปฏิทินไม่ถูกต้อง";
        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(actionBy) ||
            string.IsNullOrWhiteSpace(actionByName))
            return "กรุณากรอกชื่อวันและข้อมูลผู้บันทึกให้ครบถ้วน";
        if (dayType == "WORKING_SATURDAY" &&
            calendarDate.DayOfWeek != DayOfWeek.Saturday)
            return "วันเสาร์ทำงานต้องเลือกวันที่ตรงกับวันเสาร์เท่านั้น";

        return null;
    }

    private static WorkCalendarDayDto ReadCalendarDay(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetFieldValue<DateOnly>(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetFieldValue<DateTimeOffset>(7));
}
