namespace HrProject.Shared.Models;

public sealed record EmployeeListFilterRequest(
    int Page = 1,
    int PageSize = 10,
    string EmployeeCode = "",
    string BusinessUnit = "",
    string Department = "",
    string Position = "",
    string InternalExtension = "",
    string ResponsibilityProvince = "",
    string Brand = "",
    string CommGroup = "");

public sealed record EmployeeListItemDto(
    int Id,
    string EmployeeCode,
    string FullName,
    string InternalExtension,
    DateOnly StartDate,
    string ResponsibilityProvince,
    string Brand,
    string CommGroup);

public sealed record EmployeePagedResult(
    IReadOnlyList<EmployeeListItemDto> Items,
    int TotalItems,
    int Page,
    int PageSize);

public sealed record EmployeeListFilterOptions(
    IReadOnlyList<string> BusinessUnits,
    IReadOnlyList<string> Departments,
    IReadOnlyList<string> Positions,
    IReadOnlyList<string> ResponsibilityProvinces,
    IReadOnlyList<string> Brands,
    IReadOnlyList<string> CommGroups);
