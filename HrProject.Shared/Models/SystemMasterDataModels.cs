namespace HrProject.Shared.Models;

public sealed record MasterDataCategoryDto(
    string Code,
    string Name,
    int DisplayOrder,
    bool IsLeaveType = false,
    bool IsAttendanceEventType = false);

public sealed record MasterDataItemDto(
    long Id,
    string CategoryCode,
    string ItemCode,
    string NameTh,
    string? NameEn,
    int DisplayOrder,
    bool IsActive,
    long? ParentItemId,
    string? ParentItemName,
    DateTimeOffset UpdatedAt);

public sealed record SaveMasterDataItemRequest(
    string CategoryCode,
    string ItemCode,
    string NameTh,
    string? NameEn,
    int DisplayOrder,
    bool IsActive,
    long? ParentItemId = null);

public sealed record LeaveTypeMasterDto(
    long Id,
    string Code,
    string NameTh,
    decimal DefaultHours,
    bool DefaultBonusDeductionEnabled,
    decimal DefaultBonusDeductionPercent,
    bool IsActive,
    DateTimeOffset UpdatedAt);

public sealed record SaveLeaveTypeMasterRequest(
    string Code,
    string NameTh,
    decimal DefaultHours,
    bool DefaultBonusDeductionEnabled,
    decimal DefaultBonusDeductionPercent,
    bool IsActive);

public sealed record AttendanceEventTypeMasterDto(
    long Id,
    string Code,
    string NameTh,
    string? NameEn,
    bool CountsAsWorkTime,
    int DisplayOrder,
    bool IsActive,
    DateTimeOffset UpdatedAt);

public sealed record SaveAttendanceEventTypeMasterRequest(
    string Code,
    string NameTh,
    string? NameEn,
    bool CountsAsWorkTime,
    int DisplayOrder,
    bool IsActive);
