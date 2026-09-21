using System.Net;
using Npgsql;

namespace HrProject.Api.Services;

public sealed class LeaveCommentEmailService(
    NpgsqlDataSource dataSource,
    MicrosoftGraphMailService mailService,
    IConfiguration configuration)
{
    public async Task<IReadOnlyList<long>> LoadPendingCommentIds(int limit, CancellationToken token)
    {
        const string sql = """
            SELECT leave_comment_id
            FROM public.leave_comment_notifications
            WHERE delivery_status IN ('PENDING','FAILED')
              AND (last_attempted_at IS NULL OR last_attempted_at <= CURRENT_TIMESTAMP-INTERVAL '1 minute')
            GROUP BY leave_comment_id
            ORDER BY MIN(COALESCE(last_attempted_at,created_at)), leave_comment_id
            LIMIT @limit
            """;
        var ids = new List<long>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) ids.Add(reader.GetInt64(0));
        return ids;
    }

    public async Task SendAsync(long commentId, CancellationToken token)
    {
        var mail = await LoadMail(commentId, token);
        if (mail is null || mail.Recipients.Count == 0) return;
        if (string.IsNullOrWhiteSpace(mail.SenderEmail))
        {
            await MarkFailed(commentId, "Comment author does not have an email address.", token);
            throw new InvalidOperationException("Comment author does not have an email address.");
        }

        try
        {
            await mailService.SendAsync(
                mail.SenderEmail,
                mail.Recipients,
                $"ความคิดเห็นใหม่ในเอกสารลา {mail.DocumentNo} — {mail.RequesterName}",
                BuildBody(mail, BuildCommentUrl(commentId)),
                token);
            await MarkSent(commentId, token);
        }
        catch (Exception exception)
        {
            await MarkFailed(commentId, Trim(exception.Message, 2000), CancellationToken.None);
            throw;
        }
    }

    private async Task<MailData?> LoadMail(long commentId, CancellationToken token)
    {
        const string headerSql = """
            SELECT comment.author_name,comment.author_email,document.document_no,
                   document.creator_name,leave_type.name_th,document.leave_date,
                   document.start_time,document.leave_hours,document.status
            FROM public.leave_document_comments comment
            JOIN public.leave_documents document ON document.id=comment.leave_document_id
            JOIN public.leave_types leave_type ON leave_type.id=document.leave_type_id
            WHERE comment.id=@comment_id
            """;
        string authorName;
        string? authorEmail;
        string documentNo;
        string requesterName;
        string leaveTypeName;
        DateOnly leaveDate;
        TimeOnly startTime;
        decimal leaveHours;
        string status;
        await using (var command = dataSource.CreateCommand(headerSql))
        {
            command.Parameters.AddWithValue("comment_id", commentId);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            authorName=reader.GetString(0); authorEmail=reader.IsDBNull(1)?null:reader.GetString(1);
            documentNo=reader.GetString(2); requesterName=reader.GetString(3);
            leaveTypeName=reader.GetString(4); leaveDate=reader.GetFieldValue<DateOnly>(5);
            startTime=reader.GetFieldValue<TimeOnly>(6); leaveHours=reader.GetDecimal(7); status=reader.GetString(8);
        }

        const string recipientsSql = """
            SELECT recipient_email
            FROM public.leave_comment_notifications
            WHERE leave_comment_id=@comment_id AND delivery_status IN ('PENDING','FAILED')
            ORDER BY id
            """;
        var recipients = new List<string>();
        await using (var command = dataSource.CreateCommand(recipientsSql))
        {
            command.Parameters.AddWithValue("comment_id", commentId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) recipients.Add(reader.GetString(0));
        }

        const string threadSql = """
            SELECT item.author_name,item.comment_text,item.created_at,item.id=@comment_id
            FROM public.leave_document_comments current_comment
            JOIN public.leave_document_comments item
              ON item.leave_document_id=current_comment.leave_document_id
             AND item.created_at<=current_comment.created_at
            WHERE current_comment.id=@comment_id
            ORDER BY item.created_at,item.id
            """;
        var thread = new List<ThreadItem>();
        await using (var command = dataSource.CreateCommand(threadSql))
        {
            command.Parameters.AddWithValue("comment_id", commentId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                thread.Add(new ThreadItem(reader.GetString(0),reader.GetString(1),
                    reader.GetFieldValue<DateTimeOffset>(2),reader.GetBoolean(3)));
        }
        return new MailData(authorName,authorEmail,documentNo,requesterName,leaveTypeName,
            leaveDate,startTime,leaveHours,status,recipients,thread);
    }

    private string BuildCommentUrl(long commentId)
    {
        var clientBaseUrl = (configuration["Application:ClientBaseUrl"] ?? "http://localhost:5043")
            .TrimEnd('/');
        return $"{clientBaseUrl}/leave/comment-link/{commentId}";
    }

    private static string BuildBody(MailData mail, string commentUrl)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "-");
        var items = string.Join("", mail.Thread.Select(item =>
            $"<div style=\"margin:0 0 12px;padding:12px 14px;background:{(item.IsLatest ? "#fff7ed" : "#f8fafc")};border:1px solid {(item.IsLatest ? "#fdba74" : "#e2e8f0")};border-radius:8px\">" +
            $"<div style=\"font-weight:700;color:#172442\">{E(item.AuthorName)} <span style=\"font-weight:400;color:#64748b;font-size:12px\">{item.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm}</span></div>" +
            $"<div style=\"margin-top:6px;white-space:pre-wrap\">{E(item.Text)}</div></div>"));
        return $"""
            <div style="font-family:Arial,'Tahoma',sans-serif;color:#1e293b;line-height:1.55;max-width:760px">
              <h2 style="margin-bottom:4px;color:#172442">มีความคิดเห็นใหม่ในเอกสารการลา</h2>
              <p style="margin-top:0;color:#64748b">แสดงความคิดเห็นโดย {E(mail.AuthorName)}</p>
              <table style="border-collapse:collapse;width:100%;margin:18px 0">
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">เลขที่เอกสาร</td><td style="padding:8px;border:1px solid #e2e8f0">{E(mail.DocumentNo)}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">ผู้ขอลา</td><td style="padding:8px;border:1px solid #e2e8f0">{E(mail.RequesterName)}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">ประเภท/วันที่ลา</td><td style="padding:8px;border:1px solid #e2e8f0">{E(mail.LeaveTypeName)} · {mail.LeaveDate:dd/MM/yyyy} {mail.StartTime:HH:mm} · {mail.LeaveHours:0.##} ชั่วโมง</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">สถานะ</td><td style="padding:8px;border:1px solid #e2e8f0">{E(mail.Status)}</td></tr>
              </table>
              <h3 style="color:#172442">ประวัติการสนทนา</h3>
              {items}
              <p style="margin:20px 0">
                <a href="{E(commentUrl)}" style="display:inline-block;padding:10px 16px;color:#fff;background:#ff6633;border-radius:6px;text-decoration:none;font-weight:700">ดูเอกสารและตอบกลับ</a>
              </p>
              <p style="color:#64748b;font-size:12px">กรุณาเข้าสู่ระบบ HR เพื่อแสดงความคิดเห็นเพิ่มเติม</p>
            </div>
            """;
    }

    private async Task MarkSent(long commentId,CancellationToken token)
    {
        await using var command=dataSource.CreateCommand("UPDATE public.leave_comment_notifications SET delivery_status='SENT',retry_count=0,last_error=NULL,last_attempted_at=CURRENT_TIMESTAMP,sent_at=CURRENT_TIMESTAMP WHERE leave_comment_id=@id AND delivery_status IN ('PENDING','FAILED')");
        command.Parameters.AddWithValue("id",commentId); await command.ExecuteNonQueryAsync(token);
    }
    private async Task MarkFailed(long commentId,string error,CancellationToken token)
    {
        await using var command=dataSource.CreateCommand("UPDATE public.leave_comment_notifications SET delivery_status='FAILED',retry_count=retry_count+1,last_error=@error,last_attempted_at=CURRENT_TIMESTAMP WHERE leave_comment_id=@id AND delivery_status IN ('PENDING','FAILED')");
        command.Parameters.AddWithValue("id",commentId);command.Parameters.AddWithValue("error",error);await command.ExecuteNonQueryAsync(token);
    }
    private static string Trim(string value,int max)=>value.Length<=max?value:value[..max];
    private sealed record ThreadItem(string AuthorName,string Text,DateTimeOffset CreatedAt,bool IsLatest);
    private sealed record MailData(string AuthorName,string? SenderEmail,string DocumentNo,string RequesterName,
        string LeaveTypeName,DateOnly LeaveDate,TimeOnly StartTime,decimal LeaveHours,string Status,
        IReadOnlyList<string> Recipients,IReadOnlyList<ThreadItem> Thread);
}
