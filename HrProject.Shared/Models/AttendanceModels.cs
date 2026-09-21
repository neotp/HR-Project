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
    string? OverrideReason,
    bool IsWorkDayInProgress,
    bool IsResponseWindowOpen);

public sealed record AttendanceHistoryDto(
    long Id,
    string Action,
    string? StatusBefore,
    string StatusAfter,
    string? Details,
    string ActionBy,
    string ActionByName,
    DateTimeOffset ActionAt);

public sealed record AttendanceCommentDto(
    long Id,
    long AttendanceDailyId,
    string CommentText,
    string CommentedBy,
    string CommentedByName,
    DateTimeOffset CommentedAt);

public sealed record AddAttendanceCommentRequest(string CommentText);

public sealed record OverrideAttendanceRequest(
    string Status,
    string Reason,
    string ActionBy,
    string ActionByName);

public sealed record AttendanceResponseRequest(
    string ResponseText,
    IReadOnlyList<AttendanceAttachmentUploadDto>? Attachments,
    IReadOnlyList<string>? IssueTypes = null);

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
    IReadOnlyList<AttendanceResponseAttachmentDto> Attachments)
{
    public IReadOnlyList<AttendanceResponseIssueDto> Issues { get; init; } = [];
}

public sealed record AttendanceResponseIssueDto(
    long Id,
    string IssueType,
    string Status,
    string? ReviewedBy,
    string? ReviewedByName,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote);

public sealed record AttendanceResponseAttachmentDto(
    long Id,
    string FileName,
    string ContentType,
    long FileSizeBytes,
    DateTimeOffset UploadedAt);

public sealed record AttendanceResponseHistoryItemDto(
    DateOnly WorkDate,
    DateTime? FirstScanAt,
    DateTime? LastScanAt,
    string CurrentAttendanceStatus,
    int CurrentLateMinutes,
    int CurrentMissingMinutes,
    AttendanceResponseDto Response);

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
    int FinalLateMinutes,
    int FinalMissingMinutes,
    bool RequiresReview,
    string? ReviewReason,
    AttendanceResponseDto? LatestResponse);

public sealed record ReviewAttendanceResponseRequest(
    string Decision,
    string? ReviewNote,
    IReadOnlyList<string>? IssueTypes = null);
