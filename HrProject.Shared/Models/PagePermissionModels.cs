namespace HrProject.Shared.Models;

public sealed record PagePermissionDto(
    long PageId,
    string PageKey,
    string PageName,
    string RoutePath,
    string CategoryName,
    int DisplayOrder,
    bool CanAccess);

public sealed record SavePagePermissionItem(
    long PageId,
    bool CanAccess);

public sealed record SaveEmployeePagePermissionsRequest(
    string EmployeeId,
    IReadOnlyList<SavePagePermissionItem> Permissions,
    string UpdatedBy,
    string UpdatedByName);
