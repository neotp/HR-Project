using System.Net;
using Npgsql;

namespace HrProject.Api.Services;

public sealed record LeaveApprovalEmailItem(
    long DocumentId,
    string DocumentNo,
    DateOnly LeaveDate,
    TimeOnly StartTime,
    decimal LeaveHours);

public sealed class LeaveApprovalEmailService(
    NpgsqlDataSource dataSource,
    MicrosoftGraphMailService mailService,
    IConfiguration configuration)
{
    public async Task SendAsync(
        string creatorEmployeeId,
        string creatorName,
        string leaveTypeName,
        IReadOnlyCollection<LeaveApprovalEmailItem> items,
        string leaveReason,
        CancellationToken cancellationToken)
    {
        var addresses = await FindAddresses(
            creatorEmployeeId, cancellationToken);
        if (string.IsNullOrWhiteSpace(addresses.SenderEmail) ||
            !addresses.SenderEmail.Contains('@', StringComparison.Ordinal))
            throw new InvalidOperationException("ผู้ขอลายังไม่มี Email ในข้อมูลพนักงาน");
        if (addresses.RecipientEmails.Count == 0)
            throw new InvalidOperationException(
                "ไม่พบ Email ของ Boss หรือ Reporting To (Leave Approve) ในข้อมูลพนักงาน");

        var subject = $"ขออนุมัติลา ({leaveTypeName})";
        var body = BuildBody(creatorName, leaveTypeName, items, leaveReason);
        await mailService.SendAsync(
            addresses.SenderEmail,
            addresses.RecipientEmails,
            subject,
            body,
            cancellationToken);
    }

    private async Task<MailAddresses> FindAddresses(
        string creatorEmployeeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH requester AS
            (
                SELECT NULLIF(BTRIM(b.email_address), '') AS sender_email,
                       c.supervisor_name,
                       c.leave_approver_name
                FROM public.employees e
                JOIN public.employee_basic_info b ON b.employee_id = e.id
                JOIN public.employee_company_info c ON c.employee_id = e.id
                WHERE e.employee_code = @employee_code AND e.is_active = TRUE
                LIMIT 1
            ),
            manager_directory AS
            (
                SELECT e.employee_code,
                       NULLIF(BTRIM(b.email_address), '') AS email_address,
                       ARRAY_REMOVE(ARRAY[
                           NULLIF(REGEXP_REPLACE(UPPER(BTRIM(COALESCE(b.full_name_th, ''))), '\s+', ' ', 'g'), ''),
                           NULLIF(REGEXP_REPLACE(UPPER(BTRIM(COALESCE(b.full_name_en, ''))), '\s+', ' ', 'g'), ''),
                           NULLIF(REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', b.first_name_th, b.last_name_th))), '\s+', ' ', 'g'), ''),
                           NULLIF(REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', b.first_name_en, b.last_name_en))), '\s+', ' ', 'g'), '')
                       ], NULL) AS names
                FROM public.employees e
                JOIN public.employee_basic_info b ON b.employee_id = e.id
                WHERE e.is_active = TRUE
            )
            SELECT r.sender_email, d.email_address
            FROM requester r
            LEFT JOIN manager_directory d
              ON REGEXP_REPLACE(UPPER(BTRIM(COALESCE(r.supervisor_name, ''))), '\s+', ' ', 'g') = ANY(d.names)
              OR REGEXP_REPLACE(UPPER(BTRIM(COALESCE(r.leave_approver_name, ''))), '\s+', ' ', 'g') = ANY(d.names)
              OR UPPER(BTRIM(COALESCE(r.supervisor_name, ''))) = UPPER(d.employee_code)
              OR UPPER(BTRIM(COALESCE(r.leave_approver_name, ''))) = UPPER(d.employee_code)
            """;

        string? senderEmail = null;
        var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_code", creatorEmployeeId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
                senderEmail = reader.GetString(0);
            if (!reader.IsDBNull(1))
            {
                var email = reader.GetString(1);
                if (email.Contains('@', StringComparison.Ordinal))
                    recipients.Add(email);
            }
        }

        if (!string.IsNullOrWhiteSpace(senderEmail))
            recipients.Remove(senderEmail);
        return new MailAddresses(senderEmail, recipients.ToArray());
    }

    private string BuildBody(
        string creatorName,
        string leaveTypeName,
        IReadOnlyCollection<LeaveApprovalEmailItem> items,
        string leaveReason)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var clientBaseUrl = (configuration["Application:ClientBaseUrl"] ?? "http://localhost:5043")
            .TrimEnd('/');
        var rows = string.Join(string.Empty, items.Select(item => $$"""
            <tr>
              <td style="padding:8px;border:1px solid #e2e8f0">{{E(item.DocumentNo)}}</td>
              <td style="padding:8px;border:1px solid #e2e8f0">{{item.LeaveDate:dd/MM/yyyy}}</td>
              <td style="padding:8px;border:1px solid #e2e8f0">{{item.StartTime:HH:mm}}</td>
              <td style="padding:8px;border:1px solid #e2e8f0;text-align:right">{{item.LeaveHours:0.##}} ชั่วโมง</td>
              <td style="padding:8px;border:1px solid #e2e8f0;white-space:nowrap">
                <a href="{{E(BuildDocumentUrl(clientBaseUrl, item.DocumentId, null))}}" style="display:inline-block;padding:6px 10px;color:#1d4ed8;background:#eff6ff;border:1px solid #bfdbfe;border-radius:5px;text-decoration:none">ดูเอกสาร</a>
                <a href="{{E(BuildDocumentUrl(clientBaseUrl, item.DocumentId, "approve"))}}" style="display:inline-block;margin-left:4px;padding:6px 10px;color:#fff;background:#16a34a;border:1px solid #15803d;border-radius:5px;text-decoration:none">อนุมัติ</a>
                <a href="{{E(BuildDocumentUrl(clientBaseUrl, item.DocumentId, "reject"))}}" style="display:inline-block;margin-left:4px;padding:6px 10px;color:#fff;background:#dc2626;border:1px solid #b91c1c;border-radius:5px;text-decoration:none">ไม่อนุมัติ</a>
              </td>
            </tr>
            """));

        return $$"""
            <div style="font-family:Arial,'Tahoma',sans-serif;color:#1e293b;line-height:1.6">
              <h2 style="color:#172442">ขออนุมัติลา</h2>
              <p><strong>ผู้ขออนุมัติลา:</strong> {{E(creatorName)}}</p>
              <p><strong>ประเภทการลา:</strong> {{E(leaveTypeName)}}</p>
              <table style="border-collapse:collapse;width:100%;max-width:760px">
                <thead>
                  <tr style="background:#f1f5f9">
                    <th style="padding:8px;border:1px solid #e2e8f0;text-align:left">เลขที่เอกสาร</th>
                    <th style="padding:8px;border:1px solid #e2e8f0;text-align:left">ลาวันที่</th>
                    <th style="padding:8px;border:1px solid #e2e8f0;text-align:left">ตั้งแต่เวลา</th>
                    <th style="padding:8px;border:1px solid #e2e8f0;text-align:right">จำนวน (ชั่วโมง)</th>
                    <th style="padding:8px;border:1px solid #e2e8f0;text-align:left">ดำเนินการ</th>
                  </tr>
                </thead>
                <tbody>{{rows}}</tbody>
              </table>
              <p><strong>เหตุผล:</strong></p>
              <div style="white-space:pre-wrap;padding:10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:6px">{{E(leaveReason)}}</div>
              <p style="color:#64748b;font-size:12px">เมื่อกดปุ่ม ระบบจะเปิดหน้าเอกสารให้ตรวจสอบและยืนยันก่อนดำเนินการ</p>
              <p style="color:#64748b;font-size:12px">อีเมลนี้ส่งโดยระบบ HR หลังจากมีการสร้างเอกสารขออนุมัติลา</p>
            </div>
            """;
    }

    private static string BuildDocumentUrl(string clientBaseUrl, long documentId, string? action)
    {
        var url = $"{clientBaseUrl}/leave/pending?documentId={documentId}";
        return string.IsNullOrWhiteSpace(action) ? url : $"{url}&action={action}";
    }

    private sealed record MailAddresses(
        string? SenderEmail,
        IReadOnlyCollection<string> RecipientEmails);
}
