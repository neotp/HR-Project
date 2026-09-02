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
    bool? HasMedicalCertificate,
    LeaveEditRequestDto? PendingEditRequest,
    LeaveCancelRequestDto? PendingCancelRequest,
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
    IReadOnlyList<LeaveAttachmentUploadDto>? Attachments);

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
    IReadOnlyList<LeaveAttachmentUploadDto>? Attachments);

public sealed record LeaveAttachmentUploadDto(
    string FileName,
    string ContentType,
    byte[] Content);

public sealed record LeaveDocumentAttachmentDto(
    long Id,
    string FileName,
    string ContentType,
    long FileSizeBytes,
    DateTimeOffset UploadedAt);

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
    bool? RequestedHasMedicalCertificate,
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
    bool? HasMedicalCertificate,
    string RequestReason,
    string RequestedBy,
    string RequestedByName,
    IReadOnlyList<LeaveAttachmentUploadDto>? Attachments);

public sealed record LeaveCancelRequestDto(
    long Id,
    long LeaveDocumentId,
    string RequestReason,
    string Status,
    string RequestedBy,
    string RequestedByName,
    DateTimeOffset RequestedAt);

public sealed record LeaveDocumentHistoryDto(
    long Id,
    string Action,
    string? DetailsText,
    string ActionBy,
    string ActionByName,
    DateTimeOffset ActionAt);

public sealed record LeaveBonusDeductionDto(
    long LeaveDocumentId,
    bool IsDeducted,
    decimal DeductionPercent,
    bool IsWaived,
    string? AdjustmentReason,
    bool IsOverridden,
    string? UpdatedBy,
    string? UpdatedByName,
    DateTimeOffset? UpdatedAt);

public sealed record UpdateLeaveBonusDeductionRequest(
    bool IsDeducted,
    decimal DeductionPercent,
    bool IsWaived,
    string AdjustmentReason,
    string ActionBy,
    string ActionByName);

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

public sealed record LeaveQuotaMovementEmployeeSummaryDto(
    string EmployeeId,
    string EmployeeName,
    string Department,
    int QuotaYear,
    int LeaveTypeCount,
    decimal QuotaHours,
    decimal UsedHours,
    decimal RemainingHours,
    DateTimeOffset? LastMovementAt);

public sealed record LeaveQuotaMovementTypeSummaryDto(
    long LeaveTypeId,
    string LeaveTypeName,
    decimal QuotaHours,
    decimal UsedHours,
    decimal RemainingHours);

public sealed record LeaveQuotaMovementDto(
    long Id,
    string EmployeeId,
    long LeaveTypeId,
    string LeaveTypeName,
    int QuotaYear,
    string MovementType,
    string SourceType,
    long? SourceId,
    string? ReferenceNo,
    decimal HoursIn,
    decimal HoursOut,
    decimal BalanceBefore,
    decimal BalanceAfter,
    string? Notes,
    string ActionBy,
    string ActionByName,
    DateTimeOffset OccurredAt);

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
