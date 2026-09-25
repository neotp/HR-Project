namespace HrProject.Shared.Models;

public sealed record EmployeeFieldChangeDto(
    string FieldKey,
    string FieldName,
    string OldValue,
    string NewValue);

public sealed record EmployeeEditRequestDto(
    long Id,
    string RequestNo,
    string EmployeeId,
    string EmployeeName,
    IReadOnlyList<EmployeeFieldChangeDto> Changes,
    string RequestReason,
    string Status,
    string RequestedByName,
    DateTimeOffset RequestedAt)
{
    public string RequestedBy { get; init; } = string.Empty;
    public IReadOnlyList<EmployeeEditRequestAttachmentDto> Attachments { get; init; } = [];
    public int CommentCount { get; init; }
}

public sealed record CreateEmployeeEditRequest(
    string EmployeeId,
    string EmployeeName,
    IReadOnlyList<EmployeeFieldChangeDto> Changes,
    string RequestReason,
    string RequestedBy,
    string RequestedByName,
    IReadOnlyList<EmployeeEditRequestAttachmentUploadDto>? Attachments = null);

public sealed record EmployeeEditRequestAttachmentUploadDto(
    string FileName,
    string ContentType,
    byte[] Content);

public sealed record EmployeeEditRequestAttachmentDto(
    long Id,
    string FileName,
    string ContentType,
    long FileSizeBytes,
    DateTimeOffset UploadedAt);

public sealed record ReviewEmployeeEditRequest(
    string ReviewedBy,
    string ReviewedByName,
    string? ReviewRemark);

public sealed record UpdateEmployeeEditRequest(
    IReadOnlyList<EmployeeFieldChangeDto> Changes,
    string RequestReason,
    IReadOnlyList<EmployeeEditRequestAttachmentUploadDto>? NewAttachments = null,
    IReadOnlyList<long>? RemovedAttachmentIds = null);

public sealed record EmployeeEditRequestCommentDto(
    long Id,
    long EmployeeEditRequestId,
    string CommentText,
    string CommentedBy,
    string CommentedByName,
    DateTimeOffset CommentedAt)
{
    public IReadOnlyList<LeaveCommentAttachmentDto> Attachments { get; init; } = [];
}

public sealed record AddEmployeeEditRequestCommentRequest(
    string CommentText,
    IReadOnlyList<LeaveCommentAttachmentUploadDto>? Attachments = null);
public sealed record EmployeeEditRequestLinkDestinationDto(string Url);
