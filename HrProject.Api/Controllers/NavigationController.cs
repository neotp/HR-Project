using System.Security.Claims;
using HrProject.Api.Services;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/navigation")]
[Authorize(Policy = "HrApiScope")]
public sealed class NavigationController(
    NpgsqlDataSource dataSource,
    PageAccessService pageAccessService,
    PageActionPermissionService actionPermissionService) : ControllerBase
{
    [HttpGet("pending-counts")]
    public async Task<ActionResult<NavigationPendingCountsDto>> GetPendingCounts(
        CancellationToken cancellationToken)
    {
        var employeeId = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(employeeId)) return Unauthorized();

        var leavePendingAccessTask = pageAccessService.HasAccess(employeeId, "LEAVE_PENDING", cancellationToken);
        var leaveRevisionAccessTask = pageAccessService.HasAccess(employeeId, "LEAVE_REVISIONS", cancellationToken);
        var quotaAccessTask = pageAccessService.HasAccess(employeeId, "LEAVE_REQUEST_QUOTA", cancellationToken);
        var attendanceAccessTask = pageAccessService.HasAccess(employeeId, "ATTENDANCE_REVIEWS", cancellationToken);
        var employeeEditAccessTask = pageAccessService.HasAccess(employeeId, "EMPLOYEE_EDIT_REQUESTS", cancellationToken);
        var preEmployeeAccessTask = pageAccessService.HasAccess(employeeId, "PRE_EMPLOYEES", cancellationToken);
        var localAccountAccessTask = pageAccessService.HasAccess(employeeId, "LOCAL_ACCOUNTS", cancellationToken);

        var leavePendingActionsTask = actionPermissionService.GetAllowedActions(employeeId, "LEAVE_PENDING", cancellationToken);
        var leaveRevisionActionsTask = actionPermissionService.GetAllowedActions(employeeId, "LEAVE_REVISIONS", cancellationToken);
        var quotaActionsTask = actionPermissionService.GetAllowedActions(employeeId, "LEAVE_REQUEST_QUOTA", cancellationToken);
        var employeeEditActionsTask = actionPermissionService.GetAllowedActions(employeeId, "EMPLOYEE_EDIT_REQUESTS", cancellationToken);
        var preEmployeeActionsTask = actionPermissionService.GetAllowedActions(employeeId, "PRE_EMPLOYEES", cancellationToken);
        var localAccountActionsTask = actionPermissionService.GetAllowedActions(employeeId, "LOCAL_ACCOUNTS", cancellationToken);

        await Task.WhenAll(
            leavePendingAccessTask, leaveRevisionAccessTask, quotaAccessTask,
            attendanceAccessTask, employeeEditAccessTask, preEmployeeAccessTask,
            localAccountAccessTask, leavePendingActionsTask, leaveRevisionActionsTask,
            quotaActionsTask, employeeEditActionsTask, preEmployeeActionsTask,
            localAccountActionsTask);

        var leavePendingActions = (await leavePendingActionsTask).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leaveRevisionActions = (await leaveRevisionActionsTask).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var quotaActions = (await quotaActionsTask).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var employeeEditActions = (await employeeEditActionsTask).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preEmployeeActions = (await preEmployeeActionsTask).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localAccountActions = (await localAccountActionsTask).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var canSeeLeavePending = await leavePendingAccessTask;
        var canSeeLeaveRevisions = await leaveRevisionAccessTask;
        var canReviewQuota = await quotaAccessTask && HasReviewAction(quotaActions);
        var canReviewEmployeeEdits = await employeeEditAccessTask && HasReviewAction(employeeEditActions);
        var canReviewPreEmployees = await preEmployeeAccessTask &&
            (preEmployeeActions.Contains("CONVERT") || preEmployeeActions.Contains("EDIT") ||
             preEmployeeActions.Contains("VIEW_ALL"));
        var canReviewLocalPasswords = await localAccountAccessTask &&
            localAccountActions.Contains("RESET_PASSWORD");
        var canSeeAttendanceReviews = await attendanceAccessTask;
        var unrestrictedLeavePending = leavePendingActions.Contains("VIEW_ALL") ||
            leavePendingActions.Contains("APPROVE") || leavePendingActions.Contains("REJECT");
        var unrestrictedLeaveRevisions = leaveRevisionActions.Contains("VIEW_ALL");

        const string sql = """
            WITH document_reviewers AS
            (
                SELECT document.id, document.status,
                       reporting_approver.employee_code AS reporting_approver_code,
                       upper_reporting_approver.employee_code AS upper_approver_code,
                       document.approver_employee_id AS stored_approver_code
                FROM public.leave_documents document
                LEFT JOIN public.employees creator_employee
                       ON creator_employee.employee_code = document.creator_employee_id
                      AND creator_employee.is_active = TRUE
                LEFT JOIN public.employee_company_info creator_company
                       ON creator_company.employee_id = creator_employee.id
                LEFT JOIN LATERAL
                (
                    SELECT approver_employee.id AS employee_row_id,
                           approver_employee.employee_code
                    FROM public.employees approver_employee
                    JOIN public.employee_basic_info approver_basic
                      ON approver_basic.employee_id = approver_employee.id
                    WHERE approver_employee.is_active = TRUE
                      AND REGEXP_REPLACE(UPPER(BTRIM(COALESCE(creator_company.leave_approver_name, ''))), '\s+', ' ', 'g') IN
                          (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(approver_basic.full_name_th, ''))), '\s+', ' ', 'g'),
                           REGEXP_REPLACE(UPPER(BTRIM(COALESCE(approver_basic.full_name_en, ''))), '\s+', ' ', 'g'),
                           REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', approver_basic.first_name_th, approver_basic.last_name_th))), '\s+', ' ', 'g'),
                           REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', approver_basic.first_name_en, approver_basic.last_name_en))), '\s+', ' ', 'g'))
                    ORDER BY approver_employee.id
                    LIMIT 1
                ) reporting_approver ON TRUE
                LEFT JOIN public.employee_company_info reporting_approver_company
                       ON reporting_approver_company.employee_id = reporting_approver.employee_row_id
                LEFT JOIN LATERAL
                (
                    SELECT upper_employee.employee_code
                    FROM public.employees upper_employee
                    JOIN public.employee_basic_info upper_basic
                      ON upper_basic.employee_id = upper_employee.id
                    WHERE upper_employee.is_active = TRUE
                      AND REGEXP_REPLACE(UPPER(BTRIM(COALESCE(reporting_approver_company.leave_approver_name, ''))), '\s+', ' ', 'g') IN
                          (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(upper_basic.full_name_th, ''))), '\s+', ' ', 'g'),
                           REGEXP_REPLACE(UPPER(BTRIM(COALESCE(upper_basic.full_name_en, ''))), '\s+', ' ', 'g'),
                           REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', upper_basic.first_name_th, upper_basic.last_name_th))), '\s+', ' ', 'g'),
                           REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', upper_basic.first_name_en, upper_basic.last_name_en))), '\s+', ' ', 'g'))
                    ORDER BY upper_employee.id
                    LIMIT 1
                ) upper_reporting_approver ON TRUE
            ),
            latest_attendance_response AS
            (
                SELECT DISTINCT ON (response.attendance_daily_id)
                       response.attendance_daily_id, response.status
                FROM public.attendance_responses response
                ORDER BY response.attendance_daily_id, response.submitted_at DESC, response.id DESC
            )
            SELECT
                CASE WHEN @can_leave_pending THEN
                    (SELECT COUNT(*)::int FROM document_reviewers reviewer
                     WHERE reviewer.status = 'PENDING_APPROVAL'
                       AND (@unrestricted_leave_pending OR UPPER(@employee_id) IN
                           (UPPER(COALESCE(reviewer.reporting_approver_code, '')),
                            UPPER(COALESCE(reviewer.upper_approver_code, '')),
                            UPPER(COALESCE(reviewer.stored_approver_code, '')))))
                ELSE 0 END,
                CASE WHEN @can_leave_revisions THEN
                    (SELECT COUNT(DISTINCT reviewer.id)::int
                     FROM document_reviewers reviewer
                     LEFT JOIN public.leave_cancel_requests cancel_request
                       ON cancel_request.leave_document_id = reviewer.id
                      AND cancel_request.status = 'PENDING'
                     WHERE (reviewer.status = 'EDIT_REQUESTED' OR cancel_request.id IS NOT NULL)
                       AND (@unrestricted_leave_revisions OR UPPER(@employee_id) IN
                           (UPPER(COALESCE(reviewer.reporting_approver_code, '')),
                            UPPER(COALESCE(reviewer.upper_approver_code, '')),
                            UPPER(COALESCE(reviewer.stored_approver_code, '')))))
                ELSE 0 END,
                CASE WHEN @can_quota THEN
                    (SELECT COUNT(*)::int FROM public.leave_quota_requests WHERE status = 'PENDING')
                ELSE 0 END,
                CASE WHEN @can_attendance THEN
                    ((SELECT COUNT(*)::int
                      FROM public.attendance_daily_records daily
                      JOIN public.employees employee
                        ON employee.employee_code = daily.employee_id AND employee.is_active = TRUE
                      LEFT JOIN public.employee_company_info company ON company.employee_id = employee.id
                      JOIN latest_attendance_response response ON response.attendance_daily_id = daily.id
                      WHERE daily.work_date BETWEEN @year_start AND @year_end
                        AND response.status = 'SUBMITTED'
                        AND COALESCE(company.exclude_attendance_calculation, FALSE) = FALSE
                        AND NOT EXISTS
                        (SELECT 1 FROM public.work_calendar_days calendar
                         WHERE calendar.calendar_date = daily.work_date
                           AND calendar.day_type = 'PUBLIC_HOLIDAY')
                        AND (EXTRACT(ISODOW FROM daily.work_date) BETWEEN 1 AND 5 OR EXISTS
                        (SELECT 1 FROM public.work_calendar_days calendar
                         WHERE calendar.calendar_date = daily.work_date
                           AND calendar.day_type = 'WORKING_SATURDAY'))
                        AND (daily.calculated_late_minutes > 0 OR
                             daily.calculated_missing_minutes > 0 OR daily.requires_review = TRUE))
                     +
                     (SELECT COUNT(*)::int FROM public.attendance_calendar_events event
                      WHERE event.event_date BETWEEN @year_start AND @year_end
                        AND event.status = 'PENDING_REVIEW'))
                ELSE 0 END,
                CASE WHEN @can_employee_edits THEN
                    (SELECT COUNT(*)::int FROM public.employee_edit_requests WHERE status = 'PENDING')
                ELSE 0 END,
                CASE WHEN @can_pre_employees THEN
                    (SELECT COUNT(*)::int FROM public.pre_employees
                     WHERE status IN ('DRAFT', 'INCOMPLETE', 'READY'))
                ELSE 0 END,
                CASE WHEN @can_local_passwords THEN
                    (SELECT COUNT(*)::int FROM public.local_password_reset_requests WHERE status = 'PENDING')
                ELSE 0 END
            """;

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", employeeId.Trim());
        command.Parameters.AddWithValue("can_leave_pending", canSeeLeavePending);
        command.Parameters.AddWithValue("unrestricted_leave_pending", unrestrictedLeavePending);
        command.Parameters.AddWithValue("can_leave_revisions", canSeeLeaveRevisions);
        command.Parameters.AddWithValue("unrestricted_leave_revisions", unrestrictedLeaveRevisions);
        command.Parameters.AddWithValue("can_quota", canReviewQuota);
        command.Parameters.AddWithValue("can_attendance", canSeeAttendanceReviews);
        command.Parameters.AddWithValue("can_employee_edits", canReviewEmployeeEdits);
        command.Parameters.AddWithValue("can_pre_employees", canReviewPreEmployees);
        command.Parameters.AddWithValue("can_local_passwords", canReviewLocalPasswords);
        command.Parameters.AddWithValue("year_start", new DateOnly(today.Year, 1, 1));
        command.Parameters.AddWithValue("year_end", new DateOnly(today.Year, 12, 31));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return Ok(new NavigationPendingCountsDto(0, 0, 0, 0, 0, 0, 0));

        return Ok(new NavigationPendingCountsDto(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)));
    }

    private static bool HasReviewAction(ISet<string> actions) =>
        actions.Contains("APPROVE") || actions.Contains("REJECT") || actions.Contains("VIEW_ALL");

    private async Task<string?> ResolveAuthenticatedEmployeeId(CancellationToken cancellationToken)
    {
        var employeeId = User.FindFirstValue("employee_id");
        if (!string.IsNullOrWhiteSpace(employeeId)) return employeeId;

        var tenantId = User.FindFirstValue("tid");
        var objectId = User.FindFirstValue("oid");
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId)) return null;

        const string sql = """
            SELECT employee_id
            FROM public.microsoft_accounts
            WHERE tenant_id = @tenant_id AND entra_object_id = @object_id
              AND is_active = TRUE
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("object_id", objectId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}
