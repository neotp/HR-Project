namespace HrProject.Shared.Models;

public sealed record CurrentMicrosoftUserDto(
    string TenantId,
    string ObjectId,
    string Email,
    string DisplayName,
    string EmployeeId,
    string EmployeeName,
    string Department,
    string Position,
    string SupervisorName,
    string LeaveApproverName,
    bool IsEmployeeLinked);
