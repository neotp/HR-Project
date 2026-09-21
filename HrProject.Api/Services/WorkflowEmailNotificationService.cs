using System.Net;
using Npgsql;

namespace HrProject.Api.Services;

public sealed record WorkflowEmailRecipient(string EmployeeId, string Name, string Email);

public sealed class WorkflowEmailNotificationService(
    NpgsqlDataSource dataSource,
    PageAccessService pageAccessService,
    MicrosoftGraphMailService mailService,
    IConfiguration configuration)
{
    public async Task SendAsync(
        string pageKey,
        string senderEmployeeId,
        string title,
        string details,
        string routePath,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? recipientScope = null)
    {
        var sender = await FindEmployee(senderEmployeeId, cancellationToken);
        if (sender is null || !IsEmail(sender.Email))
            throw new InvalidOperationException($"พนักงาน {senderEmployeeId} ยังไม่มี Email สำหรับส่งการแจ้งเตือน");

        var recipients = await GetRecipientEmailsAsync(pageKey, recipientScope, cancellationToken);
        recipients.Remove(sender.Email);
        if (recipients.Count == 0) return;

        var clientBaseUrl = (configuration["Application:ClientBaseUrl"] ?? "http://localhost:5043")
            .TrimEnd('/');
        var targetUrl = $"{clientBaseUrl}/{routePath.TrimStart('/')}";
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var body = $$"""
            <div style="font-family:Arial,'Tahoma',sans-serif;color:#1e293b;line-height:1.6">
              <h2 style="color:#172442">{{E(title)}}</h2>
              <p style="white-space:pre-wrap">{{E(details)}}</p>
              <p><a href="{{E(targetUrl)}}" style="display:inline-block;padding:9px 14px;color:#fff;background:#2563eb;border-radius:6px;text-decoration:none">เปิดหน้ารายการ</a></p>
              <p style="color:#64748b;font-size:12px">คุณได้รับอีเมลนี้เนื่องจากมีสิทธิ์ Email Notification ของหน้านี้</p>
            </div>
            """;
        await mailService.SendAsync(sender.Email, recipients, title, body, cancellationToken);
    }

    public async Task<HashSet<string>> GetRecipientEmailsAsync(
        string pageKey,
        IReadOnlyCollection<string>? recipientScope,
        CancellationToken cancellationToken)
    {
        var recipients = await GetRecipientsAsync(pageKey, recipientScope, cancellationToken);
        return recipients.Select(item => item.Email)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<WorkflowEmailRecipient>> GetRecipientsAsync(
        string pageKey,
        IReadOnlyCollection<string>? recipientScope,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH target_action AS
            (
                SELECT action.id
                FROM public.application_pages page
                JOIN public.application_page_actions action
                  ON action.application_page_id = page.id AND action.is_active = TRUE
                WHERE page.page_key = @page_key AND page.is_active = TRUE
                  AND action.action_key = 'EMAIL_NOTIFICATION'
                LIMIT 1
            ),
            allowed_employee AS
            (
                SELECT permission.employee_id
                FROM public.employee_page_action_permissions permission
                JOIN target_action action ON action.id = permission.application_page_action_id
                WHERE permission.can_execute = TRUE
                UNION
                SELECT member.employee_id
                FROM public.app_role_members member
                JOIN public.app_roles role
                  ON role.id = member.app_role_id AND role.is_active = TRUE
                JOIN public.app_role_page_action_permissions permission
                  ON permission.app_role_id = role.id
                JOIN target_action action ON action.id = permission.application_page_action_id
                WHERE permission.can_execute = TRUE
            )
            SELECT employee.employee_code,
                   COALESCE(NULLIF(basic.full_name_th, ''), NULLIF(basic.full_name_en, ''), employee.employee_code),
                   NULLIF(BTRIM(basic.email_address), '')
            FROM allowed_employee allowed
            JOIN public.employees employee
              ON employee.employee_code = allowed.employee_id AND employee.is_active = TRUE
            JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
            WHERE NULLIF(BTRIM(basic.email_address), '') IS NOT NULL
              AND BTRIM(basic.email_address) LIKE '%@%'
            ORDER BY employee.employee_code
            """;
        var candidates = new List<WorkflowEmailRecipient>();
        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("page_key", pageKey.Trim().ToUpperInvariant());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                candidates.Add(new WorkflowEmailRecipient(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        var scope = recipientScope?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recipients = new List<WorkflowEmailRecipient>();
        foreach (var candidate in candidates)
        {
            if (scope is not null && !scope.Contains(candidate.EmployeeId)) continue;
            if (await pageAccessService.HasAccess(candidate.EmployeeId, pageKey, cancellationToken))
                recipients.Add(candidate);
        }
        return recipients;
    }

    private async Task<EmployeeMail?> FindEmployee(string employeeId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COALESCE(NULLIF(basic.full_name_th, ''), NULLIF(basic.full_name_en, ''), employee.employee_code),
                   NULLIF(BTRIM(basic.email_address), '')
            FROM public.employees employee
            JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
            WHERE employee.employee_code = @employee_id AND employee.is_active = TRUE
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", employeeId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new EmployeeMail(reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1))
            : null;
    }

    private static bool IsEmail(string email) => email.Contains('@', StringComparison.Ordinal);
    private sealed record EmployeeMail(string Name, string Email);
}
