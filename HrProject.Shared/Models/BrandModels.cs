namespace HrProject.Shared.Models;

public sealed record BrandDto(
    long Id,
    string Code,
    string Name,
    int DisplayOrder,
    bool IsActive,
    int EmployeeCount);

public sealed record BrandEmployeeDto(
    long EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string Department,
    string Position,
    string Email,
    int DisplayOrder);
