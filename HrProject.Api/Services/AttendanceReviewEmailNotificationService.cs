using System.Globalization;
using System.Net;
using Npgsql;

namespace HrProject.Api.Services;

public sealed class AttendanceReviewEmailNotificationService(
    NpgsqlDataSource dataSource,
    MicrosoftGraphMailService mailService,
    IConfiguration configuration)
{
    public async Task NotifySubmittedAsync(long attendanceDailyId, CancellationToken cancellationToken)
    {
        var notification = await LoadNotification(attendanceDailyId, cancellationToken);
        if (notification is null || notification.RecipientEmails.Count == 0) return;
        if (string.IsNullOrWhiteSpace(notification.SenderEmail) ||
            !notification.SenderEmail.Contains('@', StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"พนักงาน {notification.EmployeeId} ยังไม่มี Email สำหรับส่งการแจ้งเตือนข้อโต้แย้ง");

        var subject = $"มีข้อโต้แย้งการมาทำงานรอตรวจสอบ: {notification.EmployeeName} " +
                      $"({notification.WorkDate:dd/MM/yyyy})";
        await mailService.SendAsync(
            notification.SenderEmail,
            notification.RecipientEmails,
            subject,
            BuildBody(notification),
            cancellationToken);
    }

    private async Task<Notification?> LoadNotification(long attendanceDailyId, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH target_page AS
            (
                SELECT id, is_enabled
                FROM public.application_pages
                WHERE page_key = 'ATTENDANCE_REVIEWS' AND is_active = TRUE
                LIMIT 1
            ),
            target_action AS
            (
                SELECT action.id
                FROM public.application_page_actions action
                JOIN target_page page ON page.id = action.application_page_id
                WHERE action.action_key = 'EMAIL_NOTIFICATION' AND action.is_active = TRUE
                LIMIT 1
            ),
            notification_permission AS
            (
                SELECT permission.employee_id
                FROM public.employee_page_action_permissions permission
                JOIN target_action action
                  ON action.id = permission.application_page_action_id
                WHERE permission.can_execute = TRUE
                UNION
                SELECT member.employee_id
                FROM public.app_role_members member
                JOIN public.app_roles role
                  ON role.id = member.app_role_id AND role.is_active = TRUE
                JOIN public.app_role_page_action_permissions permission
                  ON permission.app_role_id = role.id
                JOIN target_action action
                  ON action.id = permission.application_page_action_id
                WHERE permission.can_execute = TRUE
            ),
            eligible_recipient AS
            (
                SELECT DISTINCT notification.employee_id
                FROM notification_permission notification
                CROSS JOIN target_page page
                WHERE page.is_enabled = TRUE
                  AND
                  (
                      EXISTS
                      (
                          SELECT 1
                          FROM public.employee_page_permissions permission
                          WHERE permission.employee_id = notification.employee_id
                            AND permission.application_page_id = page.id
                            AND permission.can_access = TRUE
                      )
                      OR EXISTS
                      (
                          SELECT 1
                          FROM public.app_role_members member
                          JOIN public.app_roles role
                            ON role.id = member.app_role_id AND role.is_active = TRUE
                          JOIN public.app_role_page_permissions permission
                            ON permission.app_role_id = role.id
                           AND permission.application_page_id = page.id
                           AND permission.can_access = TRUE
                          WHERE member.employee_id = notification.employee_id
                      )
                  )
            )
            SELECT daily.employee_id, daily.work_date, daily.final_status,
                   daily.calculated_late_minutes, daily.calculated_missing_minutes,
                   COALESCE(NULLIF(submitter_basic.full_name_th, ''),
                            NULLIF(submitter_basic.full_name_en, ''), daily.employee_id),
                   NULLIF(BTRIM(submitter_basic.email_address), ''),
                   latest_response.response_text,
                   recipient_employee.employee_code,
                   COALESCE(NULLIF(recipient_basic.full_name_th, ''),
                            NULLIF(recipient_basic.full_name_en, ''), recipient_employee.employee_code),
                   NULLIF(BTRIM(recipient_basic.email_address), '')
            FROM public.attendance_daily_records daily
            JOIN public.employees submitter_employee
              ON submitter_employee.employee_code = daily.employee_id
             AND submitter_employee.is_active = TRUE
            JOIN public.employee_basic_info submitter_basic
              ON submitter_basic.employee_id = submitter_employee.id
            JOIN LATERAL
            (
                SELECT response.response_text
                FROM public.attendance_responses response
                WHERE response.attendance_daily_id = daily.id
                  AND response.status = 'SUBMITTED'
                ORDER BY response.submitted_at DESC, response.id DESC
                LIMIT 1
            ) latest_response ON TRUE
            JOIN eligible_recipient eligible
              ON eligible.employee_id <> daily.employee_id
            JOIN public.employees recipient_employee
              ON recipient_employee.employee_code = eligible.employee_id
             AND recipient_employee.is_active = TRUE
            JOIN public.employee_basic_info recipient_basic
              ON recipient_basic.employee_id = recipient_employee.id
            WHERE daily.id = @daily_id
              AND NULLIF(BTRIM(recipient_basic.email_address), '') IS NOT NULL
              AND BTRIM(recipient_basic.email_address) LIKE '%@%'
            ORDER BY recipient_employee.employee_code
            """;

        Notification? result = null;
        var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("daily_id", attendanceDailyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result ??= new Notification(
                reader.GetString(0), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<DateOnly>(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetString(7), recipients);
            recipients.Add(reader.GetString(10));
        }
        return result;
    }

    private string BuildBody(Notification item)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var clientBaseUrl = (configuration["Application:ClientBaseUrl"] ?? "http://localhost:5043")
            .TrimEnd('/');
        var reviewUrl = $"{clientBaseUrl}/attendance/reviews";
        var workDate = item.WorkDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        return $$"""
            <div style="font-family:Arial,'Tahoma',sans-serif;color:#1e293b;line-height:1.6">
              <h2 style="color:#172442">มีข้อโต้แย้งการมาทำงานรอตรวจสอบ</h2>
              <p>พนักงานส่งข้อโต้แย้งการมาทำงานเข้ามาใหม่ โดยมีรายละเอียดดังนี้</p>
              <table style="border-collapse:collapse;width:100%;max-width:680px">
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">พนักงาน</td><td style="padding:8px;border:1px solid #e2e8f0">{{E(item.EmployeeName)}} ({{E(item.EmployeeId)}})</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">วันที่</td><td style="padding:8px;border:1px solid #e2e8f0">{{E(workDate)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">สถานะเดิม</td><td style="padding:8px;border:1px solid #e2e8f0">{{E(item.FinalStatus)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">มาสาย</td><td style="padding:8px;border:1px solid #e2e8f0">{{item.LateMinutes}} นาที</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">ขาดงาน</td><td style="padding:8px;border:1px solid #e2e8f0">{{item.MissingMinutes}} นาที</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">ข้อความโต้แย้ง</td><td style="padding:8px;border:1px solid #e2e8f0;white-space:pre-wrap">{{E(item.ResponseText)}}</td></tr>
              </table>
              <p><a href="{{E(reviewUrl)}}" style="display:inline-block;padding:9px 14px;color:#fff;background:#2563eb;border-radius:6px;text-decoration:none">เปิดหน้าตรวจสอบข้อโต้แย้ง</a></p>
              <p style="color:#64748b;font-size:12px">คุณได้รับอีเมลนี้เนื่องจากมีสิทธิ์ “รับอีเมลแจ้งเตือนข้อโต้แย้ง”</p>
            </div>
            """;
    }

    private sealed record Notification(
        string EmployeeId,
        string EmployeeName,
        string? SenderEmail,
        DateOnly WorkDate,
        string FinalStatus,
        int LateMinutes,
        int MissingMinutes,
        string ResponseText,
        IReadOnlyCollection<string> RecipientEmails);
}
