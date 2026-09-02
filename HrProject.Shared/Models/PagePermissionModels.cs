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

public sealed record PageActionPermissionDto(
    long ActionId,
    long PageId,
    string PageKey,
    string PageName,
    string CategoryName,
    string ActionKey,
    string ActionName,
    string? Description,
    int DisplayOrder,
    bool CanExecute);

public sealed record SavePageActionPermissionItem(
    long ActionId,
    bool CanExecute);

public sealed record SaveEmployeePageActionPermissionsRequest(
    string EmployeeId,
    IReadOnlyList<SavePageActionPermissionItem> Permissions,
    string UpdatedBy,
    string UpdatedByName);

public sealed record CurrentPageActionPermissionsDto(
    string PageKey,
    IReadOnlyList<string> AllowedActions);

public sealed record CurrentPageAccessDto(
    string PageKey,
    bool CanAccess,
    bool GrantedByBusinessRule,
    bool GrantedByExplicitPermission);

public sealed record ApplicationPageAvailabilityDto(
    long PageId,
    string PageKey,
    string PageName,
    string RoutePath,
    string CategoryName,
    int DisplayOrder,
    bool IsEnabled,
    DateTimeOffset? UpdatedAt,
    string? UpdatedByName);

public sealed record SaveApplicationPageAvailabilityItem(
    long PageId,
    bool IsEnabled);

public sealed record SaveApplicationPageAvailabilityRequest(
    IReadOnlyList<SaveApplicationPageAvailabilityItem> Pages,
    string UpdatedBy,
    string UpdatedByName);

public sealed record AppRoleDto(
    long Id,
    string RoleKey,
    string RoleName,
    string? Description,
    bool IsActive,
    IReadOnlyList<string> MemberEmployeeIds);

public sealed record CreateAppRoleRequest(
    string RoleName,
    string? Description);

public sealed record SaveAppRoleMembersRequest(
    IReadOnlyList<string> EmployeeIds);

public sealed record SaveRolePagePermissionsRequest(
    IReadOnlyList<SavePagePermissionItem> Permissions,
    string UpdatedBy,
    string UpdatedByName);

public sealed record SaveRolePageActionPermissionsRequest(
    IReadOnlyList<SavePageActionPermissionItem> Permissions,
    string UpdatedBy,
    string UpdatedByName);
