using System.Text.Json;
using System.Text.RegularExpressions;
using HrProject.Api.Services;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/work-calendar-templates")]
public sealed class WorkCalendarTemplatesController(
    NpgsqlDataSource dataSource,
    PageAccessService pageAccessService) : ControllerBase
{
    private static readonly HashSet<string> ValidTypes =
        new(StringComparer.OrdinalIgnoreCase) { "PUBLIC_HOLIDAY", "WORKING_SATURDAY" };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkCalendarTemplateDto>>> GetVersions(
        [FromQuery] string templateType, CancellationToken cancellationToken)
    {
        var error = await ValidateEditor(templateType, cancellationToken);
        if (error is not null) return error;
        return Ok(await LoadVersions(templateType.ToUpperInvariant(), cancellationToken));
    }

    [HttpGet("published/{templateType}")]
    public async Task<ActionResult<WorkCalendarTemplateDto>> GetPublished(
        string templateType, CancellationToken cancellationToken)
    {
        if (!ValidTypes.Contains(templateType)) return BadRequest("ประเภท Template ไม่ถูกต้อง");
        const string sql = """
            SELECT id, template_type, template_name, version_no, is_published,
                   settings::text, created_by, created_by_name, created_at
            FROM public.work_calendar_document_templates
            WHERE template_type = @type
            ORDER BY is_published DESC, version_no DESC
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("type", templateType.ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Ok(ReadTemplate(reader)) : NotFound();
    }

    [HttpPost]
    public async Task<ActionResult<WorkCalendarTemplateDto>> SaveDraft(
        SaveWorkCalendarTemplateRequest request, CancellationToken cancellationToken)
    {
        var error = await ValidateEditor(request.TemplateType, cancellationToken);
        if (error is not null) return error;
        var settingsError = ValidateSettings(request.TemplateName, request.Settings);
        if (settingsError is not null) return BadRequest(settingsError);
        var actor = await GetAuthenticatedEmployee(cancellationToken);
        if (actor is null) return Unauthorized();

        const string sql = """
            INSERT INTO public.work_calendar_document_templates
                (template_type, template_name, version_no, is_published,
                 settings, created_by, created_by_name)
            VALUES
                (@type, @name,
                 (SELECT COALESCE(MAX(version_no), 0) + 1
                  FROM public.work_calendar_document_templates WHERE template_type = @type),
                 FALSE, CAST(@settings AS jsonb), @by, @by_name)
            RETURNING id, template_type, template_name, version_no, is_published,
                      settings::text, created_by, created_by_name, created_at
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("type", request.TemplateType.ToUpperInvariant());
        command.Parameters.AddWithValue("name", request.TemplateName.Trim());
        command.Parameters.AddWithValue("settings", JsonSerializer.Serialize(request.Settings, JsonOptions));
        command.Parameters.AddWithValue("by", actor.Value.EmployeeId);
        command.Parameters.AddWithValue("by_name", actor.Value.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var result = ReadTemplate(reader);
        return Created($"api/work-calendar-templates/{result.Id}", result);
    }

    [HttpPost("{id:long}/publish")]
    public async Task<ActionResult<WorkCalendarTemplateDto>> Publish(
        long id, CancellationToken cancellationToken)
    {
        var actor = await GetAuthenticatedEmployee(cancellationToken);
        if (actor is null) return Unauthorized();
        const string typeSql = "SELECT template_type FROM public.work_calendar_document_templates WHERE id = @id";
        await using var typeCommand = dataSource.CreateCommand(typeSql);
        typeCommand.Parameters.AddWithValue("id", id);
        var templateType = (string?)await typeCommand.ExecuteScalarAsync(cancellationToken);
        if (templateType is null) return NotFound();
        if (!await pageAccessService.HasAccess(actor.Value.EmployeeId, "WORK_CALENDAR", cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์จัดการ Template เอกสาร");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var clear = new NpgsqlCommand(
            "UPDATE public.work_calendar_document_templates SET is_published = FALSE WHERE template_type = @type",
            connection, transaction))
        {
            clear.Parameters.AddWithValue("type", templateType);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var publish = new NpgsqlCommand(
            "UPDATE public.work_calendar_document_templates SET is_published = TRUE WHERE id = @id",
            connection, transaction))
        {
            publish.Parameters.AddWithValue("id", id);
            await publish.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        var versions = await LoadVersions(templateType, cancellationToken);
        return Ok(versions.First(item => item.Id == id));
    }

    private async Task<List<WorkCalendarTemplateDto>> LoadVersions(string type, CancellationToken token)
    {
        const string sql = """
            SELECT id, template_type, template_name, version_no, is_published,
                   settings::text, created_by, created_by_name, created_at
            FROM public.work_calendar_document_templates
            WHERE template_type = @type
            ORDER BY version_no DESC
            """;
        var result = new List<WorkCalendarTemplateDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("type", type);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(ReadTemplate(reader));
        return result;
    }

    private static WorkCalendarTemplateDto ReadTemplate(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4),
        JsonSerializer.Deserialize<WorkCalendarTemplateSettings>(reader.GetString(5), JsonOptions) ?? new(),
        reader.GetString(6), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8));

    private async Task<ActionResult?> ValidateEditor(string templateType, CancellationToken token)
    {
        if (!ValidTypes.Contains(templateType)) return BadRequest("ประเภท Template ไม่ถูกต้อง");
        var actor = await GetAuthenticatedEmployee(token);
        if (actor is null) return Unauthorized();
        return await pageAccessService.HasAccess(actor.Value.EmployeeId, "WORK_CALENDAR", token)
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์จัดการ Template เอกสาร");
    }

    private static string? ValidateSettings(string name, WorkCalendarTemplateSettings settings)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) return "กรุณาระบุชื่อ Template ไม่เกิน 200 ตัวอักษร";
        if (settings is null) return "ไม่พบการตั้งค่า Template";
        if (settings.PageMarginMm is < 8 or > 30) return "ระยะขอบต้องอยู่ระหว่าง 8-30 มม.";
        if (settings.BaseFontSizePt is < 7 or > 18) return "ขนาดตัวอักษรต้องอยู่ระหว่าง 7-18 pt";
        if (settings.ListSpacingMm is < 0 or > 8) return "ระยะห่างรายการต้องอยู่ระหว่าง 0-8 มม.";
        if (!IsHexColor(settings.HeaderBandColor) || !IsHexColor(settings.AccentColor) || !IsHexColor(settings.TitleColor))
            return "รูปแบบสีใน Template ไม่ถูกต้อง";
        if (settings.LogoPosition is not ("LEFT" or "RIGHT")) return "ตำแหน่งโลโก้ไม่ถูกต้อง";
        if (settings.TitleAlignment is not ("LEFT" or "CENTER" or "RIGHT")) return "การจัดแนวหัวข้อไม่ถูกต้อง";
        if (settings.TitleThai?.Length > 500 || settings.TitleEnglish?.Length > 500 ||
            settings.IntroText?.Length > 3000 || settings.PolicyText?.Length > 3000 ||
            settings.FooterThai?.Length > 2000 || settings.FooterEnglish?.Length > 2000)
            return "ข้อความใน Template ยาวเกินกำหนด";
        return null;
    }

    private static bool IsHexColor(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);

    private async Task<(string EmployeeId, string Name)?> GetAuthenticatedEmployee(CancellationToken token)
    {
        var tenantId = User.FindFirst("tid")?.Value;
        var objectId = User.FindFirst("oid")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId)) return null;
        const string sql = """
            SELECT employee_id, COALESCE(NULLIF(display_name, ''), employee_id)
            FROM public.microsoft_accounts
            WHERE tenant_id = @tenant_id AND entra_object_id = @object_id
              AND is_active = TRUE AND employee_id IS NOT NULL
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("object_id", objectId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? (reader.GetString(0), reader.GetString(1)) : null;
    }
}
