using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace HrProject.Api.Services;

public sealed class MicrosoftGraphMailService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration)
{
    public async Task SendAsync(
        string senderEmail,
        string recipientEmail,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken)
    {
        var tenantId = Required("AzureAd:TenantId");
        var clientId = Required("AzureAd:ClientId");
        var clientSecret = Required("AzureAd:ClientSecret");
        var client = httpClientFactory.CreateClient();

        using var tokenResponse = await client.PostAsync(
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials"
            }), cancellationToken);

        var tokenJson = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"ขอสิทธิ์ส่งอีเมลจาก Microsoft Graph ไม่สำเร็จ ({(int)tokenResponse.StatusCode})");

        var accessToken = JsonDocument.Parse(tokenJson).RootElement
            .GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Microsoft Graph ไม่ได้ส่ง access token กลับมา");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(senderEmail)}/sendMail");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            message = new
            {
                subject,
                body = new { contentType = "HTML", content = htmlBody },
                toRecipients = new[]
                {
                    new { emailAddress = new { address = recipientEmail } }
                }
            },
            saveToSentItems = true
        });

        using var sendResponse = await client.SendAsync(request, cancellationToken);
        if (!sendResponse.IsSuccessStatusCode)
        {
            var responseText = await sendResponse.Content.ReadAsStringAsync(cancellationToken);
            var safeDetail = responseText.Length > 500 ? responseText[..500] : responseText;
            throw new InvalidOperationException(
                $"Microsoft Graph ส่งอีเมลไม่สำเร็จ ({(int)sendResponse.StatusCode}): {safeDetail}");
        }
    }

    public static string BuildBody(
        string senderName,
        string recipientName,
        DateOnly leaveDate,
        TimeOnly startTime,
        TimeOnly endTime,
        decimal leaveHours,
        string note)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var dateText = leaveDate.ToString("dd/MM/yyyy");
        var timeText = $"{startTime:HH:mm} - {endTime:HH:mm}";

        return $$"""
            <div style="font-family:Arial,'Tahoma',sans-serif;color:#1e293b;line-height:1.6">
              <h2 style="color:#172442">แจ้งข้อมูลจากหัวหน้างาน</h2>
              <p>เรียน {{E(recipientName)}}</p>
              <table style="border-collapse:collapse;width:100%;max-width:680px">
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">วันที่</td><td style="padding:8px;border:1px solid #e2e8f0">{{E(dateText)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">เวลา</td><td style="padding:8px;border:1px solid #e2e8f0">{{E(timeText)}}</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">จำนวนชั่วโมง</td><td style="padding:8px;border:1px solid #e2e8f0">{{leaveHours.ToString("0.##")}} ชั่วโมง</td></tr>
                <tr><td style="padding:8px;border:1px solid #e2e8f0;font-weight:bold">หมายเหตุ</td><td style="padding:8px;border:1px solid #e2e8f0;white-space:pre-wrap">{{E(string.IsNullOrWhiteSpace(note) ? "-" : note)}}</td></tr>
              </table>
              <p>ผู้แจ้ง: {{E(senderName)}}</p>
              <p style="color:#64748b;font-size:12px">อีเมลนี้เป็นการแจ้งข้อมูลเท่านั้น ไม่ใช่เอกสารลาและไม่มีผลต่อโควตาวันลา</p>
            </div>
            """;
    }

    private string Required(string key) => configuration[key]
        ?? throw new InvalidOperationException($"ยังไม่ได้ตั้งค่า {key}");
}
