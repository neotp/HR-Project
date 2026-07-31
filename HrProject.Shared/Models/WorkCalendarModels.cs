namespace HrProject.Shared.Models;

public sealed record WorkCalendarDayDto(
    long Id,
    DateOnly CalendarDate,
    string DayType,
    string Name,
    string? Note,
    string UpdatedBy,
    string UpdatedByName,
    DateTimeOffset UpdatedAt);

public sealed record SaveWorkCalendarDayRequest(
    DateOnly CalendarDate,
    string DayType,
    string Name,
    string? Note,
    string ActionBy,
    string ActionByName);

public sealed record SaveWorkCalendarDayBatchItem(
    DateOnly CalendarDate,
    string Name,
    string? Note);

public sealed record SaveWorkCalendarDaysBatchRequest(
    string DayType,
    IReadOnlyList<SaveWorkCalendarDayBatchItem> Items,
    string ActionBy,
    string ActionByName);
