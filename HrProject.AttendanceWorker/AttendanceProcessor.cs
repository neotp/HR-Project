using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HrProject.AttendanceWorker;

public sealed class AttendanceProcessor(
    NpgsqlDataSource dataSource,
    IOptions<AttendanceWorkerOptions> options,
    ILogger<AttendanceProcessor> logger)
{
    private static readonly TimeOnly WorkStart = new(9, 0);
    private static readonly TimeOnly LunchStart = new(12, 0);
    private static readonly TimeOnly LunchEnd = new(13, 0);
    private static readonly TimeOnly WorkEnd = new(18, 0);
    private readonly AttendanceWorkerOptions settings = options.Value;

    public async Task RecalculateAsync(HashSet<AttendanceKey> affected, CancellationToken token)
    {
        var queued = await AddQueuedRecalculations(affected, token);
        var today = DateOnly.FromDateTime(LocalNow());
        var yesterday = today.AddDays(-1);
        // Attendance is a result of an elapsed work day. Calendar changes may
        // queue future dates, but those dates must not become absent/late yet.
        await DeleteFutureDailyRecords(today, token);
        // Rebuild today on every cycle and yesterday for all employees so that
        // a stopped worker can safely resume before final daily calculation.
        await AddEmployeesWithScans(affected, today, token);
        await AddAllActiveEmployees(affected, yesterday, token);
        // Old status values are recalculated once and disappear from this query.
        await AddLegacyStatusRecords(affected, token);
        await KeepEligibleEmployeesOnly(affected, token);

        foreach (var discarded in queued.Where(key => !affected.Contains(key)))
            await CompleteQueuedRecalculation(discarded, token);

        foreach (var key in affected.OrderBy(x => x.WorkDate).ThenBy(x => x.EmployeeId))
        {
            try
            {
                await RecalculateOne(key, token);
                if (queued.Contains(key))
                    await CompleteQueuedRecalculation(key, token);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Attendance calculation failed for {EmployeeId} on {WorkDate}",
                    key.EmployeeId, key.WorkDate);
                if (queued.Contains(key))
                    await FailQueuedRecalculation(key, exception.Message, token);
            }
        }
    }

    private async Task<HashSet<AttendanceKey>> AddQueuedRecalculations(
        HashSet<AttendanceKey> affected, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT employee_id, work_date
            FROM public.attendance_recalculation_queue
            WHERE attempts < 10
            ORDER BY requested_at, employee_id, work_date
            LIMIT 1000
            """);
        var queued = new HashSet<AttendanceKey>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var key = new AttendanceKey(reader.GetString(0), reader.GetFieldValue<DateOnly>(1));
            queued.Add(key);
            affected.Add(key);
        }
        return queued;
    }

    private async Task CompleteQueuedRecalculation(AttendanceKey key, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM public.attendance_recalculation_queue
            WHERE employee_id = @employee_id AND work_date = @work_date
            """);
        command.Parameters.AddWithValue("employee_id", key.EmployeeId);
        command.Parameters.AddWithValue("work_date", key.WorkDate);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task FailQueuedRecalculation(
        AttendanceKey key, string error, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.attendance_recalculation_queue
            SET attempts = attempts + 1, last_error = @error,
                requested_at = CURRENT_TIMESTAMP
            WHERE employee_id = @employee_id AND work_date = @work_date
            """);
        command.Parameters.AddWithValue("employee_id", key.EmployeeId);
        command.Parameters.AddWithValue("work_date", key.WorkDate);
        command.Parameters.AddWithValue("error", error.Length > 4000 ? error[..4000] : error);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task AddEmployeesWithScans(
        HashSet<AttendanceKey> affected, DateOnly workDate, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT DISTINCT source_employee_id
            FROM public.attendance_raw_scans
            WHERE captured_at::date = @work_date
            """);
        command.Parameters.AddWithValue("work_date", workDate);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            affected.Add(new AttendanceKey(reader.GetString(0), workDate));
    }

    private async Task AddAllActiveEmployees(
        HashSet<AttendanceKey> affected, DateOnly workDate, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT employee.employee_code
            FROM public.employees employee
            LEFT JOIN public.employee_company_info company ON company.employee_id = employee.id
            WHERE employee.is_active = TRUE
              AND COALESCE(company.exclude_attendance_calculation, FALSE) = FALSE
            """);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            affected.Add(new AttendanceKey(reader.GetString(0), workDate));
    }

    private async Task AddLegacyStatusRecords(HashSet<AttendanceKey> affected, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT employee_id, work_date
            FROM public.attendance_daily_records
            WHERE calculated_status IN ('LEAVE','INCOMPLETE','REVIEW_REQUIRED','NO_DATA')
            """);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            affected.Add(new AttendanceKey(reader.GetString(0), reader.GetFieldValue<DateOnly>(1)));
    }

    private async Task KeepEligibleEmployeesOnly(HashSet<AttendanceKey> affected, CancellationToken token)
    {
        if (affected.Count == 0)
            return;

        await using var command = dataSource.CreateCommand("""
            SELECT employee.employee_code
            FROM public.employees employee
            LEFT JOIN public.employee_company_info company ON company.employee_id = employee.id
            WHERE employee.is_active = TRUE
              AND COALESCE(company.exclude_attendance_calculation, FALSE) = FALSE
            """);
        var eligible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            eligible.Add(reader.GetString(0));

        // Raw scans are retained for audit, but unknown, inactive, and explicitly
        // excluded employees must never produce attendance daily documents.
        affected.RemoveWhere(key => !eligible.Contains(key.EmployeeId));
    }

    private async Task RecalculateOne(AttendanceKey key, CancellationToken token)
    {
        var today = DateOnly.FromDateTime(LocalNow());
        if (key.WorkDate > today)
        {
            await DeleteDailyRecord(key, token);
            return;
        }

        var dayContext = await LoadDayContext(key, token);
        if (!dayContext.IsWorkDay)
        {
            // Removing a working Saturday or adding a public holiday must also
            // remove a previously calculated attendance document for that day.
            await DeleteDailyRecord(key, token);
            return;
        }

        var result = Calculate(key.WorkDate, dayContext);
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);

        string? statusBefore = null;
        bool hasOverride = false;
        const string existingSql = """
            SELECT calculated_status, overridden_by IS NOT NULL
            FROM public.attendance_daily_records
            WHERE employee_id = @employee_id AND work_date = @work_date
            FOR UPDATE
            """;
        await using (var existing = new NpgsqlCommand(existingSql, connection, transaction))
        {
            existing.Parameters.AddWithValue("employee_id", key.EmployeeId);
            existing.Parameters.AddWithValue("work_date", key.WorkDate);
            await using var reader = await existing.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                statusBefore = reader.GetString(0);
                hasOverride = reader.GetBoolean(1);
            }
        }

        const string upsertSql = """
            INSERT INTO public.attendance_daily_records
                (employee_id, work_date, first_scan_at, last_scan_at, scan_count,
                 calculated_status, final_status, late_minutes, missing_minutes,
                 calculated_late_minutes, calculated_missing_minutes,
                 requires_review, review_reason, calculation_detail, calculated_at, updated_at)
            VALUES
                (@employee_id, @work_date, @first_scan, @last_scan, @scan_count,
                 @status, @status, @late_minutes, @missing_minutes,
                 @late_minutes, @missing_minutes,
                 @requires_review, @review_reason, CAST(@detail AS jsonb),
                 CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ON CONFLICT (employee_id, work_date) DO UPDATE SET
                first_scan_at = EXCLUDED.first_scan_at, last_scan_at = EXCLUDED.last_scan_at,
                scan_count = EXCLUDED.scan_count, calculated_status = EXCLUDED.calculated_status,
                calculated_late_minutes = EXCLUDED.calculated_late_minutes,
                calculated_missing_minutes = EXCLUDED.calculated_missing_minutes,
                final_status = CASE WHEN attendance_daily_records.overridden_by IS NULL
                                    THEN EXCLUDED.final_status ELSE attendance_daily_records.final_status END,
                late_minutes = CASE WHEN attendance_daily_records.overridden_by IS NULL
                                    THEN EXCLUDED.late_minutes ELSE attendance_daily_records.late_minutes END,
                missing_minutes = CASE WHEN attendance_daily_records.overridden_by IS NULL
                                       THEN EXCLUDED.missing_minutes ELSE attendance_daily_records.missing_minutes END,
                requires_review = CASE WHEN attendance_daily_records.overridden_by IS NULL
                    THEN EXCLUDED.requires_review OR EXISTS
                    (
                        SELECT 1 FROM public.attendance_responses response
                        WHERE response.attendance_daily_id = attendance_daily_records.id
                          AND response.status = 'SUBMITTED'
                    )
                    ELSE FALSE END,
                review_reason = EXCLUDED.review_reason,
                calculation_detail = EXCLUDED.calculation_detail,
                calculated_at = CURRENT_TIMESTAMP, updated_at = CURRENT_TIMESTAMP
            RETURNING id
            """;
        long dailyId;
        await using (var upsert = new NpgsqlCommand(upsertSql, connection, transaction))
        {
            upsert.Parameters.AddWithValue("employee_id", key.EmployeeId);
            upsert.Parameters.AddWithValue("work_date", key.WorkDate);
            upsert.Parameters.Add(new NpgsqlParameter<DateTime?>("first_scan", dayContext.FirstScan));
            upsert.Parameters.Add(new NpgsqlParameter<DateTime?>("last_scan", dayContext.LastScan));
            upsert.Parameters.AddWithValue("scan_count", dayContext.ScanCount);
            upsert.Parameters.AddWithValue("status", result.Status);
            upsert.Parameters.AddWithValue("late_minutes", result.LateMinutes);
            upsert.Parameters.AddWithValue("missing_minutes", result.MissingMinutes);
            upsert.Parameters.AddWithValue("requires_review", result.RequiresReview && !hasOverride);
            upsert.Parameters.Add(new NpgsqlParameter<string?>("review_reason", result.Reason));
            upsert.Parameters.AddWithValue("detail", JsonSerializer.Serialize(new
            {
                firstScan = dayContext.FirstScan,
                lastScan = dayContext.LastScan,
                dayContext.ScanCount,
                approvedLeaves = dayContext.Leaves,
                attendanceEvents = dayContext.Events,
                result.Reason
            }));
            dailyId = (long)(await upsert.ExecuteScalarAsync(token))!;
        }

        if (!string.Equals(statusBefore, result.Status, StringComparison.Ordinal))
        {
            const string historySql = """
                INSERT INTO public.attendance_daily_history
                    (attendance_daily_id, action, status_before, status_after,
                     details, action_by, action_by_name)
                VALUES (@id, @action, @before, @after, @details, 'SYSTEM', 'Attendance Worker')
                """;
            await using var history = new NpgsqlCommand(historySql, connection, transaction);
            history.Parameters.AddWithValue("id", dailyId);
            history.Parameters.AddWithValue("action", statusBefore is null ? "AUTO_CALCULATE" : "AUTO_RECALCULATE");
            history.Parameters.Add(new NpgsqlParameter<string?>("before", statusBefore));
            history.Parameters.AddWithValue("after", result.Status);
            history.Parameters.Add(new NpgsqlParameter<string?>("details", result.Reason));
            await history.ExecuteNonQueryAsync(token);
        }

        await transaction.CommitAsync(token);
    }

    private async Task DeleteFutureDailyRecords(DateOnly today, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM public.attendance_daily_records
            WHERE work_date > @today
            """);
        command.Parameters.AddWithValue("today", today);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task DeleteDailyRecord(AttendanceKey key, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM public.attendance_daily_records
            WHERE employee_id = @employee_id AND work_date = @work_date
            """);
        command.Parameters.AddWithValue("employee_id", key.EmployeeId);
        command.Parameters.AddWithValue("work_date", key.WorkDate);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<DayContext> LoadDayContext(AttendanceKey key, CancellationToken token)
    {
        const string sql = """
            SELECT
                (SELECT MIN(captured_at) FROM public.attendance_raw_scans
                  WHERE source_employee_id = @employee_id AND captured_at::date = @work_date),
                (SELECT MAX(captured_at) FROM public.attendance_raw_scans
                  WHERE source_employee_id = @employee_id AND captured_at::date = @work_date),
                (SELECT COUNT(*)::int FROM public.attendance_raw_scans
                  WHERE source_employee_id = @employee_id AND captured_at::date = @work_date),
                CASE
                    WHEN EXISTS (SELECT 1 FROM public.work_calendar_days
                                 WHERE calendar_date = @work_date AND day_type = 'PUBLIC_HOLIDAY') THEN FALSE
                    WHEN EXISTS (SELECT 1 FROM public.work_calendar_days
                                 WHERE calendar_date = @work_date AND day_type = 'WORKING_SATURDAY') THEN TRUE
                    WHEN EXTRACT(ISODOW FROM @work_date::date) BETWEEN 1 AND 5 THEN TRUE
                    ELSE FALSE
                END,
                CASE
                    WHEN EXISTS (SELECT 1 FROM public.work_calendar_days
                                 WHERE calendar_date = @work_date AND day_type = 'WORKING_SATURDAY')
                    THEN TIME '17:00'
                    ELSE TIME '18:00'
                END
            """;
        DateTime? first;
        DateTime? last;
        int count;
        bool isWorkDay;
        TimeOnly workEnd;
        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("employee_id", key.EmployeeId);
            command.Parameters.AddWithValue("work_date", key.WorkDate);
            await using var reader = await command.ExecuteReaderAsync(token);
            await reader.ReadAsync(token);
            first = reader.IsDBNull(0) ? null : reader.GetDateTime(0);
            last = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
            count = reader.GetInt32(2);
            isWorkDay = reader.GetBoolean(3);
            workEnd = reader.GetFieldValue<TimeOnly>(4);
        }

        const string leaveSql = """
            SELECT start_time, leave_hours, document_no
            FROM public.leave_documents
            WHERE creator_employee_id = @employee_id AND leave_date = @work_date
              AND status IN ('APPROVED', 'EDIT_REQUESTED')
            ORDER BY start_time, id
            """;
        var leaves = new List<ApprovedLeave>();
        await using (var command = dataSource.CreateCommand(leaveSql))
        {
            command.Parameters.AddWithValue("employee_id", key.EmployeeId);
            command.Parameters.AddWithValue("work_date", key.WorkDate);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var start = reader.GetFieldValue<TimeOnly>(0);
                var hours = reader.GetDecimal(1);
                leaves.Add(new ApprovedLeave(start, AddWorkingHours(start, hours), hours, reader.GetString(2)));
            }
        }

        const string eventSql = """
            SELECT event.start_time, event.end_time, event.event_type,
                   COALESCE(NULLIF(event.title, ''), event_type.name_th),
                   EXISTS
                   (
                       SELECT 1
                       FROM public.attendance_raw_scans scan
                       WHERE scan.source_employee_id = event.employee_id
                         AND scan.captured_at::date = event.event_date
                         AND scan.captured_at::time >= event.start_time
                         AND scan.captured_at::time < event.end_time
                   ) AS has_office_scan
            FROM public.attendance_calendar_events event
            JOIN public.attendance_event_types event_type
              ON event_type.code = event.event_type
            WHERE event.employee_id = @employee_id AND event.event_date = @work_date
              AND event_type.counts_as_work_time = TRUE
              AND event.status = 'APPROVED'
            ORDER BY event.start_time, event.id
            """;
        var events = new List<AttendanceEvent>();
        await using (var command = dataSource.CreateCommand(eventSql))
        {
            command.Parameters.AddWithValue("employee_id", key.EmployeeId);
            command.Parameters.AddWithValue("work_date", key.WorkDate);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                events.Add(new AttendanceEvent(reader.GetFieldValue<TimeOnly>(0),
                    reader.GetFieldValue<TimeOnly>(1), reader.GetString(2), reader.GetString(3),
                    reader.GetBoolean(4)));
        }
        return new DayContext(first, last, count, isWorkDay, workEnd, leaves, events);
    }

    private CalculationResult Calculate(DateOnly workDate, DayContext context)
    {
        var now = LocalNow();
        var isTodayBeforeEnd = workDate == DateOnly.FromDateTime(now) && TimeOnly.FromDateTime(now) < context.WorkEnd;
        var requiresReview = context.Leaves.Any(x => x.Start > WorkStart && x.End < context.WorkEnd);
        var coverages = context.Leaves
            .Select(item => new CoveredInterval(item.Start, item.End))
            .Concat(context.Events
                .Where(item => !item.HasOfficeScan)
                .Select(item => new CoveredInterval(item.Start, item.End)))
            .ToList();
        var eventSummary = BuildEventSummary(context.Events);

        if (context.ScanCount == 0)
        {
            if (isTodayBeforeEnd)
                return new("IN_PROGRESS", 0, 0, requiresReview,
                    AppendEventSummary(requiresReview
                        ? "ยังไม่พบข้อมูลสแกนและมีใบลาระหว่างวันที่ต้องตรวจสอบ"
                        : "ยังไม่พบข้อมูลสแกนระหว่างวันทำงาน", eventSummary));

            var absentMinutes = (int)Math.Ceiling(UncoveredWorkingMinutes(WorkStart, context.WorkEnd, context.WorkEnd, coverages));
            return absentMinutes > 0
                ? new("ABSENT", 0, absentMinutes, requiresReview,
                    AppendEventSummary(BuildReason(0, absentMinutes, requiresReview), eventSummary))
                : new("PRESENT", 0, 0, requiresReview,
                    AppendEventSummary(BuildReason(0, 0, requiresReview), eventSummary));
        }

        var first = TimeOnly.FromDateTime(context.FirstScan!.Value);
        var last = TimeOnly.FromDateTime(context.LastScan!.Value);
        var meetsFullOfficeScan = first < new TimeOnly(9, 1) && last >= context.WorkEnd;
        // A face scan inside an attendance event proves that the employee was at
        // the company during that event. That event can no longer cover lateness
        // or missing time; other non-overlapping events and approved leave remain valid.
        var effectiveCoverages = coverages;
        var uncoveredBeforeFirst = UncoveredWorkingMinutes(WorkStart, first, context.WorkEnd, effectiveCoverages);
        // The first complete uncovered minute is late: 09:00:59 is on time,
        // while 09:01:00 starts at one late minute.
        var lateMinutes = uncoveredBeforeFirst >= 1
            ? Math.Max(1, (int)Math.Floor(uncoveredBeforeFirst))
            : 0;
        var missingMinutes = isTodayBeforeEnd
            ? 0
            : (int)Math.Ceiling(UncoveredWorkingMinutes(last, context.WorkEnd, context.WorkEnd, effectiveCoverages));

        var status = isTodayBeforeEnd
            ? (lateMinutes > 0 ? "LATE" : "IN_PROGRESS")
            : missingMinutes > 0
                ? "ABSENT"
                : lateMinutes > 0 ? "LATE" : "PRESENT";
        var reason = BuildReason(lateMinutes, missingMinutes, requiresReview, isTodayBeforeEnd);
        reason = meetsFullOfficeScan
            ? $"{reason} · ทำงานที่บริษัท"
            : AppendEventSummary(reason, eventSummary);
        if (!meetsFullOfficeScan && context.Events.Any(item => item.HasOfficeScan))
            reason = $"{reason} · พบสแกนในช่วง Event จึงคำนวณตามเวลาทำงานที่บริษัท";
        return new(status, lateMinutes, missingMinutes, requiresReview, reason);
    }

    private static double UncoveredWorkingMinutes(
        TimeOnly rangeStart, TimeOnly rangeEnd, TimeOnly workEnd,
        IReadOnlyList<CoveredInterval> coverages)
    {
        if (rangeEnd <= rangeStart) return 0;
        return Math.Max(0,
            UncoveredSegmentMinutes(rangeStart, rangeEnd, WorkStart, LunchStart, coverages) +
            UncoveredSegmentMinutes(rangeStart, rangeEnd, LunchEnd, workEnd, coverages));
    }

    private static double UncoveredSegmentMinutes(
        TimeOnly rangeStart, TimeOnly rangeEnd,
        TimeOnly segmentStart, TimeOnly segmentEnd,
        IReadOnlyList<CoveredInterval> coverages)
    {
        var start = rangeStart > segmentStart ? rangeStart : segmentStart;
        var end = rangeEnd < segmentEnd ? rangeEnd : segmentEnd;
        if (end <= start) return 0;

        var covered = coverages
            .Select(coverage => (
                Start: coverage.Start > start ? coverage.Start : start,
                End: coverage.End < end ? coverage.End : end))
            .Where(interval => interval.End > interval.Start)
            .OrderBy(interval => interval.Start)
            .ToList();
        var coveredMinutes = 0d;
        TimeOnly? currentStart = null;
        TimeOnly? currentEnd = null;
        foreach (var interval in covered)
        {
            if (currentStart is null)
            {
                currentStart = interval.Start;
                currentEnd = interval.End;
            }
            else if (interval.Start <= currentEnd!.Value)
            {
                if (interval.End > currentEnd.Value) currentEnd = interval.End;
            }
            else
            {
                coveredMinutes += MinutesBetween(currentStart.Value, currentEnd!.Value);
                currentStart = interval.Start;
                currentEnd = interval.End;
            }
        }
        if (currentStart is not null)
            coveredMinutes += MinutesBetween(currentStart.Value, currentEnd!.Value);
        return MinutesBetween(start, end) - coveredMinutes;
    }

    private static double MinutesBetween(TimeOnly start, TimeOnly end) =>
        TimeSpan.FromTicks(end.Ticks - start.Ticks).TotalMinutes;

    private static string BuildReason(
        int lateMinutes, int missingMinutes, bool requiresReview, bool isInProgress = false)
    {
        var parts = new List<string>();
        if (lateMinutes > 0) parts.Add($"มาสาย {lateMinutes} นาที");
        if (missingMinutes > 0) parts.Add($"ขาดงาน {missingMinutes} นาที");
        if (parts.Count == 0) parts.Add(isInProgress ? "อยู่ในช่วงเวลาทำงาน" : "มาปกติ");
        if (requiresReview) parts.Add("รอตรวจสอบข้อมูลลาระหว่างวัน");
        return string.Join(" · ", parts);
    }

    private static string? BuildEventSummary(IReadOnlyList<AttendanceEvent> events)
    {
        if (events.Count == 0) return null;
        return string.Join(", ", events.Select(item =>
            $"{item.Title} {item.Start:HH\\:mm}–{item.End:HH\\:mm}"));
    }

    private static string AppendEventSummary(string reason, string? eventSummary) =>
        string.IsNullOrWhiteSpace(eventSummary) ? reason : $"{reason} · {eventSummary}";

    private DateTime LocalNow()
    {
        try { return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, settings.TimeZone).DateTime; }
        catch { return DateTime.UtcNow.AddHours(7); }
    }

    private static TimeOnly AddWorkingHours(TimeOnly start, decimal hours)
    {
        var remaining = (int)Math.Round(hours * 60, MidpointRounding.AwayFromZero);
        var current = start >= LunchStart && start < LunchEnd ? LunchEnd : start;
        if (current < LunchStart)
        {
            var beforeLunch = (int)(LunchStart.ToTimeSpan() - current.ToTimeSpan()).TotalMinutes;
            if (remaining <= beforeLunch) return current.AddMinutes(remaining);
            remaining -= beforeLunch;
            current = LunchEnd;
        }
        return current.AddMinutes(remaining);
    }

    private sealed record DayContext(
        DateTime? FirstScan, DateTime? LastScan, int ScanCount,
        bool IsWorkDay, TimeOnly WorkEnd, IReadOnlyList<ApprovedLeave> Leaves,
        IReadOnlyList<AttendanceEvent> Events);
    private sealed record ApprovedLeave(TimeOnly Start, TimeOnly End, decimal Hours, string DocumentNo);
    private sealed record AttendanceEvent(
        TimeOnly Start, TimeOnly End, string EventType, string Title, bool HasOfficeScan);
    private sealed record CoveredInterval(TimeOnly Start, TimeOnly End);
    private sealed record CalculationResult(
        string Status, int LateMinutes, int MissingMinutes, bool RequiresReview, string Reason);
}
