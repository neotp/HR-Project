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
    DateTimeOffset RequestedAt);

public sealed record CreateEmployeeEditRequest(
    string EmployeeId,
    string EmployeeName,
    IReadOnlyList<EmployeeFieldChangeDto> Changes,
    string RequestReason,
    string RequestedBy,
    string RequestedByName);

public sealed record ReviewEmployeeEditRequest(
    string ReviewedBy,
    string ReviewedByName,
    string? ReviewRemark);
