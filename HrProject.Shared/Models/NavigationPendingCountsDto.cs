namespace HrProject.Shared.Models;

public sealed record NavigationPendingCountsDto(
    int LeavePending,
    int LeaveRevisions,
    int LeaveQuotaRequests,
    int AttendanceReviews,
    int EmployeeEditRequests,
    int PreEmployees,
    int LocalPasswordResetRequests);
