using System.Globalization;
using System.Net;
using Npgsql;

namespace HrProject.Api.Services;

public sealed class LeaveCancellationEmailService(
    NpgsqlDataSource dataSource,
    MicrosoftGraphMailService mailService)
{
    public async Task SendAsync(
        long documentId,
        string cancelledByName,
        string? cancellationReason,
        CancellationToken cancellationToken)
    {
        var details = await FindDetails(documentId, cancellationToken)
            ?? throw new InvalidOperationException("ไม่พบข้อมูลเอกสารการลาที่ถูกยกเลิก");

        if (string.IsNullOrWhiteSpace(details.SenderEmail) ||
            !details.SenderEmail.Contains('@', StringComparison.Ordinal))
            throw new InvalidOperationException($"ผู้ขอลา {details.EmployeeId} ยังไม่มี Email");
        if (details.ManagerEmails.Count == 0)
            throw new InvalidOperationException(
                "ไม่พบ Email ของ Boss หรือ Reporting To (Leave Approve) ในข้อมูลพนักงาน");

        var subject = $"แจ้งยกเลิกเอกสารลา ({details.LeaveTypeName})";
        var body = BuildBody(details, cancelledByName, cancellationReason);
        await mailService.SendAsync(
            details.SenderEmail,
            details.ManagerEmails,
            subject,
            body,
            cancellationToken);
    }

    private async Task<MailDetails?> FindDetails(
        long documentId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH document AS
            (
                SELECT d.id, d.document_no, d.creator_employee_id, d.creator_name,
                       NULLIF(BTRIM(requester_basic.email_address), '') AS sender_email,
                       requester_company.supervisor_name,
                       requester_company.leave_approver_name,
                       leave_type.name_th AS leave_type_name,
                       d.leave_date, d.start_time, d.leave_hours, d.leave_reason
                FROM public.leave_documents d
                JOIN public.leave_types leave_type ON leave_type.id = d.leave_type_id
                LEFT JOIN public.employees requester
                  ON requester.employee_code = d.creator_employee_id
                LEFT JOIN public.employee_basic_info requester_basic
                  ON requester_basic.employee_id = requester.id
                LEFT JOIN public.employee_company_info requester_company
                  ON requester_company.employee_id = requester.id
                WHERE d.id = @document_id
                LIMIT 1
            ),
            manager_directory AS
            (
                SELECT employee.employee_code,
                       NULLIF(BTRIM(basic.email_address), '') AS email_address,
                       ARRAY_REMOVE(ARRAY[
                           NULLIF(REGEXP_REPLACE(UPPER(BTRIM(COALESCE(basic.full_name_th, ''))), '\s+', ' ', 'g'), ''),
                           NULLIF(REGEXP_REPLACE(UPPER(BTRIM(COALESCE(basic.full_name_en, ''))), '\s+', ' ', 'g'), ''),
                           NULLIF(REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', basic.first_name_th, basic.last_name_th))), '\s+', ' ', 'g'), ''),
                           NULLIF(REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', basic.first_name_en, basic.last_name_en))), '\s+', ' ', 'g'), '')
                       ], NULL) AS names
                FROM public.employees employee
                JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
                WHERE employee.is_active = TRUE
            )
            SELECT document.id, document.document_no, document.creator_employee_id,
                   document.creator_name, document.sender_email,
                   document.leave_type_name, document.leave_date, document.start_time,
                   document.leave_hours, document.leave_reason, manager.email_address
            FROM document
            LEFT JOIN manager_directory manager
              ON REGEXP_REPLACE(UPPER(BTRIM(COALESCE(document.supervisor_name, ''))), '\s+', ' ', 'g') = ANY(manager.names)
              OR REGEXP_REPLACE(UPPER(BTRIM(COALESCE(document.leave_approver_name, ''))), '\s+', ' ', 'g') = ANY(manager.names)
              OR UPPER(BTRIM(COALESCE(document.supervisor_name, ''))) = UPPER(manager.employee_code)
              OR UPPER(BTRIM(COALESCE(document.leave_approver_name, ''))) = UPPER(manager.employee_code)
            """;

        MailDetails? result = null;
        var managerEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("document_id", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result ??= new MailDetails(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), reader.GetFieldValue<DateOnly>(6),
                reader.GetFieldValue<TimeOnly>(7), reader.GetDecimal(8),
                reader.GetString(9), managerEmails);

            if (!reader.IsDBNull(10))
            {
                var email = reader.GetString(10).Trim();
                if (email.Contains('@', StringComparison.Ordinal))
                    managerEmails.Add(email);
            }
        }

        if (!string.IsNullOrWhiteSpace(result?.SenderEmail))
            managerEmails.Remove(result.SenderEmail);
        return result;
    }

    private static string BuildBody(
        MailDetails details,
        string cancelledByName,
        string? cancellationReason)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var dateText = details.LeaveDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var timeText = details.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        var reason = string.IsNullOrWhiteSpace(cancellationReason)
            ? "-"
            : cancellationReason.Trim();

        return $$"""
            <div style="font-family:Arial,'Tahoma',sans-serif;color:#1e293b;line-height:1.6">
              <h2 style="color:#b91c1c">แจ้งยกเลิกเอกสารลา</h2>
              <p>เอกสารการลาของ <strong>{{E(details.EmployeeName)}}</strong> ถูกยกเลิกแล้ว</p>
              <table style="border-collapse:collapse;width:100%;max-width:680px">
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">เลขที่เอกสาร</td><td style="padding:8px;border:1px solid #e2e8f0">{{E(details.DocumentNo)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">ประเภทการลา</td><td style="padding:8px;border:1px solid #e2e8f0">{{E(details.LeaveTypeName)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">วันที่ลา</td><td style="padding:8px;border:1px solid #e2e8f0">{{E(dateText)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">เวลาเริ่มต้น</td><td style="padding:8px;border:1px solid #e2e8f0">{{E(timeText)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">จำนวน</td><td style="padding:8px;border:1px solid #e2e8f0">{{details.LeaveHours.ToString("0.##", CultureInfo.InvariantCulture)}} ชั่วโมง</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">เหตุผลการลา</td><td style="padding:8px;border:1px solid #e2e8f0;white-space:pre-wrap">{{E(details.LeaveReason)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">ยกเลิกโดย</td><td style="padding:8px;border:1px solid #e2e8f0">{{E(cancelledByName)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">เหตุผล/หมายเหตุการยกเลิก</td><td style="padding:8px;border:1px solid #e2e8f0;white-space:pre-wrap">{{E(reason)}}</td></tr>
              </table>
              <p style="color:#64748b;font-size:12px">อีเมลนี้ส่งโดยระบบ HR หลังจากเอกสารการลาถูกยกเลิก</p>
            </div>
            """;
    }

    private sealed record MailDetails(
        long DocumentId,
        string DocumentNo,
        string EmployeeId,
        string EmployeeName,
        string? SenderEmail,
        string LeaveTypeName,
        DateOnly LeaveDate,
        TimeOnly StartTime,
        decimal LeaveHours,
        string LeaveReason,
        IReadOnlyCollection<string> ManagerEmails);
}
