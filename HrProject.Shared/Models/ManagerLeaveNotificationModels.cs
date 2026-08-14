namespace HrProject.Shared.Models;

public sealed record ManagerNotificationRecipientDto(
    string EmployeeId,
    string EmployeeName,
    string Email,
    string Department,
    string Position,
    bool IsDirectBoss,
    bool IsLeaveApprover);

public sealed record CreateManagerLeaveNotificationRequest(
    string RecipientEmployeeId,
    DateOnly LeaveDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    decimal LeaveHours,
    string? Note);

public sealed record ManagerLeaveNotificationDto(
    long Id,
    string NotificationNo,
    string SenderEmployeeId,
    string SenderName,
    string SenderEmail,
    string RecipientEmployeeId,
    string RecipientName,
    string RecipientEmail,
    DateOnly LeaveDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    decimal? LeaveHours,
    string Note,
    string EmailStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    string? ErrorMessage);
