namespace HrProject.Shared.Models;

public sealed record AttendanceDailyDto(
    long Id,
    string EmployeeId,
    DateOnly WorkDate,
    DateTime? FirstScanAt,
    DateTime? LastScanAt,
    int ScanCount,
    string CalculatedStatus,
    string FinalStatus,
    int LateMinutes,
    int MissingMinutes,
    bool RequiresReview,
    string? ReviewReason,
    DateTimeOffset CalculatedAt,
    string? OverrideReason);

public sealed record AttendanceHistoryDto(
    long Id,
    string Action,
    string? StatusBefore,
    string StatusAfter,
    string? Details,
    string ActionBy,
    string ActionByName,
    DateTimeOffset ActionAt);

public sealed record OverrideAttendanceRequest(
    string Status,
    string Reason,
    string ActionBy,
    string ActionByName);

public sealed record AttendanceResponseRequest(
    string ResponseText,
    IReadOnlyList<AttendanceAttachmentUploadDto>? Attachments);

public sealed record AttendanceAttachmentUploadDto(
    string FileName,
    string ContentType,
    byte[] Content);

public sealed record AttendanceResponseDto(
    long Id,
    long AttendanceDailyId,
    string ResponseText,
    string Status,
    string SubmittedBy,
    string SubmittedByName,
    DateTimeOffset SubmittedAt,
    IReadOnlyList<AttendanceResponseAttachmentDto> Attachments);

public sealed record AttendanceResponseAttachmentDto(
    long Id,
    string FileName,
    string ContentType,
    long FileSizeBytes,
    DateTimeOffset UploadedAt);

public sealed record AttendanceReviewItemDto(
    long AttendanceDailyId,
    string EmployeeId,
    string EmployeeName,
    string Department,
    DateOnly WorkDate,
    DateTime? FirstScanAt,
    DateTime? LastScanAt,
    string CalculatedStatus,
    string FinalStatus,
    int CalculatedLateMinutes,
    int CalculatedMissingMinutes,
    bool RequiresReview,
    string? ReviewReason,
    AttendanceResponseDto? LatestResponse);

public sealed record ReviewAttendanceResponseRequest(
    string Decision,
    string? ReviewNote);
