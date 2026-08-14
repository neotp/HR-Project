namespace HrProject.Shared.Models;

public sealed record LeaveTypeDto(
    long Id,
    string Code,
    string NameTh,
    decimal DefaultHours);

public sealed record LeaveDocumentDto(
    long Id,
    string DocumentNo,
    string CreatorEmployeeId,
    string CreatorName,
    string CreatorDepartment,
    string? ApproverEmployeeId,
    string ApproverName,
    long LeaveTypeId,
    string LeaveTypeCode,
    string LeaveTypeName,
    string LeaveKind,
    DateOnly LeaveDate,
    TimeOnly StartTime,
    decimal LeaveHours,
    string LeaveReason,
    string Status,
    DateTimeOffset CreatedAt,
    LeaveEditRequestDto? PendingEditRequest,
    bool CanCurrentUserReview = false);

public sealed record CreateLeaveDocumentRequest(
    long LeaveTypeId,
    string LeaveKind,
    DateOnly LeaveDate,
    TimeOnly StartTime,
    decimal LeaveHours,
    string LeaveReason,
    string CreatorEmployeeId,
    string CreatorName,
    string CreatorDepartment,
    string? ApproverEmployeeId,
    string ApproverName,
    bool? HasMedicalCertificate,
    LeaveAttachmentUploadDto? Attachment);

public sealed record CreateMultiDayLeaveDocumentsRequest(
    long LeaveTypeId,
    string LeaveKind,
    IReadOnlyList<CreateMultiDayLeaveItemRequest> Items,
    string LeaveReason,
    string CreatorEmployeeId,
    string CreatorName,
    string CreatorDepartment,
    string? ApproverEmployeeId,
    string ApproverName,
    bool? HasMedicalCertificate,
    LeaveAttachmentUploadDto? Attachment);

public sealed record LeaveAttachmentUploadDto(
    string FileName,
    string ContentType,
    byte[] Content);

public sealed record CreateMultiDayLeaveItemRequest(
    DateOnly LeaveDate,
    TimeOnly StartTime,
    decimal LeaveHours);

public sealed record UpdateLeaveDocumentRequest(
    long LeaveTypeId,
    string LeaveKind,
    DateOnly LeaveDate,
    TimeOnly StartTime,
    decimal LeaveHours,
    string LeaveReason,
    string ActionBy,
    string ActionByName);

public sealed record ReviewLeaveDocumentRequest(
    string ActionBy,
    string ActionByName,
    string? Remark = null);

public sealed record LeaveEditRequestDto(
    long Id,
    long LeaveDocumentId,
    long RequestedLeaveTypeId,
    string RequestedLeaveTypeName,
    string RequestedLeaveKind,
    DateOnly RequestedLeaveDate,
    TimeOnly RequestedStartTime,
    decimal RequestedLeaveHours,
    string RequestReason,
    string Status,
    string RequestedBy,
    string RequestedByName,
    DateTimeOffset RequestedAt);

public sealed record SaveLeaveEditRequest(
    long LeaveTypeId,
    string LeaveKind,
    DateOnly LeaveDate,
    TimeOnly StartTime,
    decimal LeaveHours,
    string RequestReason,
    string RequestedBy,
    string RequestedByName);

public sealed record LeaveDocumentHistoryDto(
    long Id,
    string Action,
    string? DetailsText,
    string ActionBy,
    string ActionByName,
    DateTimeOffset ActionAt);

public sealed record LeaveQuotaDto(
    long Id,
    string EmployeeId,
    long LeaveTypeId,
    string LeaveTypeName,
    int QuotaYear,
    decimal QuotaHours,
    decimal UsedHours,
    decimal RemainingHours,
    string? Notes,
    DateTimeOffset UpdatedAt);

public sealed record SaveLeaveQuotaRequest(
    string EmployeeId,
    long LeaveTypeId,
    int QuotaYear,
    decimal QuotaHours,
    string? Notes,
    string ActionBy,
    string ActionByName);

public sealed record LeaveQuotaRequestDto(
    long Id,
    string RequestNo,
    string EmployeeId,
    long LeaveTypeId,
    string LeaveTypeName,
    int QuotaYear,
    decimal RequestedHours,
    decimal? ApprovedHours,
    string RequestReason,
    string Status,
    string RequestedByName,
    DateTimeOffset RequestedAt);

public sealed record CreateLeaveQuotaRequest(
    string EmployeeId,
    long LeaveTypeId,
    int QuotaYear,
    decimal RequestedHours,
    string RequestReason,
    string RequestedBy,
    string RequestedByName);

public sealed record LeaveQuotaRequestEmployeeItem(
    string EmployeeId,
    long LeaveTypeId,
    decimal RequestedHours,
    string RequestReason);

public sealed record CreateMultiEmployeeLeaveQuotaRequest(
    IReadOnlyList<LeaveQuotaRequestEmployeeItem> Employees,
    int QuotaYear,
    string RequestedBy,
    string RequestedByName);

public sealed record ReviewLeaveQuotaRequest(
    decimal? ApprovedHours,
    string? Remark,
    string ReviewedBy,
    string ReviewedByName);
