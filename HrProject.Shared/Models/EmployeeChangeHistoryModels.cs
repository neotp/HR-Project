namespace HrProject.Shared.Models;

public sealed record EmployeeChangeHistoryDto(
    long RequestId,
    string RequestNo,
    string Status,
    string RequestReason,
    string RequestedBy,
    string RequestedByName,
    DateTimeOffset RequestedAt,
    string? ReviewedBy,
    string? ReviewedByName,
    DateTimeOffset? ReviewedAt,
    string? ReviewRemark,
    IReadOnlyList<EmployeeFieldChangeDto> Changes,
    IReadOnlyList<EmployeeChangeHistoryActionDto> Actions)
{
    public string HistoryType { get; init; } = "EDIT_REQUEST";
}

public sealed record EmployeeChangeHistoryActionDto(
    long Id,
    string Action,
    string Details,
    string ActionBy,
    string ActionByName,
    DateTimeOffset ActionAt);
