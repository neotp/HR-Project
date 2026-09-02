using System.Globalization;
using System.Net;
using Npgsql;

namespace HrProject.Api.Services;

public sealed class LeaveDecisionEmailService(
    NpgsqlDataSource dataSource,
    MicrosoftGraphMailService mailService,
    IConfiguration configuration)
{
    public async Task SendAsync(
        long documentId,
        bool approved,
        string reviewerEmployeeId,
        string reviewerName,
        string? remark,
        CancellationToken cancellationToken)
    {
        var mail = await FindMailDetails(documentId, reviewerEmployeeId, cancellationToken)
            ?? throw new InvalidOperationException("ไม่พบข้อมูลเอกสารการลา");

        if (string.IsNullOrWhiteSpace(mail.RequesterEmail) ||
            !mail.RequesterEmail.Contains('@', StringComparison.Ordinal))
            throw new InvalidOperationException($"ผู้ขอลา {mail.RequesterEmployeeId} ยังไม่มี Email");

        if (string.IsNullOrWhiteSpace(mail.ReviewerEmail) ||
            !mail.ReviewerEmail.Contains('@', StringComparison.Ordinal))
            throw new InvalidOperationException($"ผู้พิจารณา {reviewerEmployeeId} ยังไม่มี Email");

        var resultText = approved ? "อนุมัติ" : "ไม่อนุมัติ";
        var subject = $"ผลการพิจารณาใบลา: {resultText} ({mail.LeaveTypeName})";
        var body = BuildBody(mail, resultText, reviewerName, remark);
        await mailService.SendAsync(
            mail.ReviewerEmail,
            mail.RequesterEmail,
            subject,
            body,
            cancellationToken);
    }

    private async Task<MailDetails?> FindMailDetails(
        long documentId,
        string reviewerEmployeeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.id, d.document_no, d.creator_employee_id, d.creator_name,
                   requester_basic.email_address, t.name_th, d.leave_date,
                   d.start_time, d.leave_hours, d.leave_reason,
                   reviewer_basic.email_address
            FROM public.leave_documents d
            JOIN public.leave_types t ON t.id = d.leave_type_id
            LEFT JOIN public.employees requester
                   ON requester.employee_code = d.creator_employee_id
            LEFT JOIN public.employee_basic_info requester_basic
                   ON requester_basic.employee_id = requester.id
            LEFT JOIN public.employees reviewer
                   ON reviewer.employee_code = @reviewer_employee_id
            LEFT JOIN public.employee_basic_info reviewer_basic
                   ON reviewer_basic.employee_id = reviewer.id
            WHERE d.id = @document_id
            LIMIT 1
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("reviewer_employee_id", reviewerEmployeeId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new MailDetails(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetFieldValue<DateOnly>(6),
            reader.GetFieldValue<TimeOnly>(7),
            reader.GetDecimal(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private string BuildBody(
        MailDetails mail,
        string resultText,
        string reviewerName,
        string? remark)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var clientBaseUrl = (configuration["Application:ClientBaseUrl"] ?? "http://localhost:5043")
            .TrimEnd('/');
        var documentUrl = $"{clientBaseUrl}/leave/documents?documentId={mail.DocumentId}";
        var resultColor = resultText == "อนุมัติ" ? "#15803d" : "#b91c1c";
        var dateText = mail.LeaveDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var timeText = mail.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture);

        return $$"""
            <div style="font-family:Arial,'Tahoma',sans-serif;color:#1e293b;line-height:1.6">
              <h2 style="color:{{resultColor}}">ผลการพิจารณาใบลา: {{E(resultText)}}</h2>
              <p>เรียน {{E(mail.RequesterName)}}</p>
              <table style="border-collapse:collapse;width:100%;max-width:680px">
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">เลขที่เอกสาร</td><td style="padding:8px;border:1px solid #e2e8f0">{{E(mail.DocumentNo)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">ประเภทการลา</td><td style="padding:8px;border:1px solid #e2e8f0">{{E(mail.LeaveTypeName)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">วันที่ลา</td><td style="padding:8px;border:1px solid #e2e8f0">{{E(dateText)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">เวลาเริ่มต้น</td><td style="padding:8px;border:1px solid #e2e8f0">{{E(timeText)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">จำนวน</td><td style="padding:8px;border:1px solid #e2e8f0">{{mail.LeaveHours.ToString("0.##", CultureInfo.InvariantCulture)}} ชั่วโมง</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">เหตุผลการลา</td><td style="padding:8px;border:1px solid #e2e8f0;white-space:pre-wrap">{{E(mail.LeaveReason)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">ผู้พิจารณา</td><td style="padding:8px;border:1px solid #e2e8f0">{{E(reviewerName)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">เหตุผล/หมายเหตุ</td><td style="padding:8px;border:1px solid #e2e8f0;white-space:pre-wrap">{{E(string.IsNullOrWhiteSpace(remark) ? "-" : remark.Trim())}}</td></tr>
              </table>
              <p><a href="{{E(documentUrl)}}" style="display:inline-block;padding:9px 14px;color:#fff;background:#2563eb;border-radius:6px;text-decoration:none">ดูเอกสารการลา</a></p>
              <p style="color:#64748b;font-size:12px">อีเมลนี้ส่งโดยระบบ HR หลังจากมีการพิจารณาเอกสารการลา</p>
            </div>
            """;
    }

    private sealed record MailDetails(
        long DocumentId,
        string DocumentNo,
        string RequesterEmployeeId,
        string RequesterName,
        string? RequesterEmail,
        string LeaveTypeName,
        DateOnly LeaveDate,
        TimeOnly StartTime,
        decimal LeaveHours,
        string LeaveReason,
        string? ReviewerEmail);
}
