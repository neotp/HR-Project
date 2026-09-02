namespace HrProject.Shared.Models;

public sealed record AttendanceCalendarEventDto(
    long Id,
    string EmployeeId,
    DateOnly EventDate,
    string EventType,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? Title,
    string? Details,
    string Status,
    string? CreatedBy,
    string? CreatedByName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ReviewedBy,
    string? ReviewedByName,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote);

public sealed record SaveAttendanceCalendarEventRequest(
    DateOnly EventDate,
    string EventType,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? Title,
    string? Details);

public sealed record AttendanceCalendarEventReviewDto(
    long Id,
    string EmployeeId,
    string EmployeeName,
    string Department,
    DateOnly EventDate,
    string EventType,
    string EventTypeName,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? Title,
    string? Details,
    string Status,
    string? CreatedBy,
    string? CreatedByName,
    DateTimeOffset CreatedAt,
    string? ReviewedBy,
    string? ReviewedByName,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote);

public sealed record ReviewAttendanceCalendarEventRequest(
    string Decision,
    string? ReviewNote);
