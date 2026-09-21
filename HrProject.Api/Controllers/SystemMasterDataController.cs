using HrProject.Shared.Models;
using HrProject.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/system-master-data")]
public sealed class SystemMasterDataController(
    NpgsqlDataSource dataSource,
    PageAccessService pageAccessService,
    PageActionPermissionService actionPermissionService) : ControllerBase
{
    private static readonly MasterDataCategoryDto[] Categories =
    [
        new("BUSINESS_UNIT", "Business Unit", 10),
        new("DEPARTMENT", "แผนก", 20),
        new("LEAVE_TYPE", "ประเภทการลา", 30, true),
        new("LEAVE_KIND", "ชนิดการลา", 40),
        new("POSITION", "ตำแหน่ง", 50),
        new("COMPANY", "บริษัท", 60),
        new("EMPLOYMENT_TYPE", "ประเภทพนักงาน", 70),
        new("WORK_SCHEDULE", "เวลาทำงาน", 80),
        new("WORK_LOCATION", "สถานที่ทำงาน", 90),
        new("TITLE", "คำนำหน้าชื่อ", 100),
        new("RELIGION", "ศาสนา", 110),
        new("BLOOD_TYPE", "กรุ๊ปเลือด", 120),
        new("MARITAL_STATUS", "สถานภาพสมรส", 130),
        new("PROVINCE", "จังหวัด", 140),
        new("DISTRICT", "อำเภอ / เขต", 150),
        new("SUBDISTRICT", "ตำบล / แขวง", 160),
        new("POSTAL_CODE", "รหัสไปรษณีย์", 170),
        new("ATTENDANCE_EVENT_TYPE", "ประเภท Event การมาทำงาน", 180, false, true)
    ];

    [HttpGet("categories")]
    public ActionResult<IReadOnlyList<MasterDataCategoryDto>> GetCategories() => Ok(Categories);

    [HttpGet("items")]
    public async Task<ActionResult<IReadOnlyList<MasterDataItemDto>>> GetItems(
        [FromQuery] string category,
        [FromQuery] bool includeInactive = true,
        [FromQuery] long? parentItemId = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsGenericCategory(category))
            return BadRequest("ไม่พบหมวดข้อมูลพื้นฐานที่ระบุ");

        var sql = """
            SELECT item.id, item.category_code, item.item_code, item.name_th, item.name_en,
                   item.display_order, item.is_active, item.parent_item_id,
                   parent.name_th, item.updated_at
            FROM public.system_master_items item
            LEFT JOIN public.system_master_items parent ON parent.id = item.parent_item_id
            WHERE item.category_code = @category
              AND (@include_inactive OR item.is_active = TRUE)
            """;
        if (parentItemId.HasValue)
            sql += " AND item.parent_item_id = @parent_item_id";
        sql += " ORDER BY item.display_order, parent.name_th, item.name_th, item.id";
        var result = new List<MasterDataItemDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("category", category.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("include_inactive", includeInactive);
        if (parentItemId.HasValue)
            command.Parameters.AddWithValue("parent_item_id", parentItemId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new MasterDataItemDto(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5), reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetFieldValue<DateTimeOffset>(9)));
        }
        return Ok(result);
    }

    [HttpGet("items/all")]
    public async Task<ActionResult<IReadOnlyList<MasterDataItemDto>>> GetAllItems(
        [FromQuery] bool includeInactive = false,
        [FromQuery] string? categories = null,
        CancellationToken cancellationToken = default)
    {
        var requestedCategories = (categories ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requestedCategories.Any(category => !IsGenericCategory(category)))
            return BadRequest("พบหมวดข้อมูลพื้นฐานที่ไม่ถูกต้อง");

        var sql = """
            SELECT item.id, item.category_code, item.item_code, item.name_th, item.name_en,
                   item.display_order, item.is_active, item.parent_item_id,
                   parent.name_th, item.updated_at
            FROM public.system_master_items item
            LEFT JOIN public.system_master_items parent ON parent.id = item.parent_item_id
            WHERE (@include_inactive OR item.is_active = TRUE)
            """;
        if (requestedCategories.Length > 0)
            sql += " AND item.category_code = ANY(@categories)";
        sql += " ORDER BY item.category_code, item.display_order, parent.name_th, item.name_th, item.id";
        var result = new List<MasterDataItemDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("include_inactive", includeInactive);
        if (requestedCategories.Length > 0)
            command.Parameters.AddWithValue("categories", requestedCategories);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new MasterDataItemDto(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5), reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetFieldValue<DateTimeOffset>(9)));
        }
        return Ok(result);
    }

    [HttpPost("items")]
    public Task<ActionResult<MasterDataItemDto>> CreateItem(
        SaveMasterDataItemRequest request,
        CancellationToken cancellationToken) => SaveItem(null, request, cancellationToken);

    [HttpPut("items/{id:long}")]
    public Task<ActionResult<MasterDataItemDto>> UpdateItem(
        long id,
        SaveMasterDataItemRequest request,
        CancellationToken cancellationToken) => SaveItem(id, request, cancellationToken);

    [HttpGet("leave-types")]
    public async Task<ActionResult<IReadOnlyList<LeaveTypeMasterDto>>> GetLeaveTypes(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, code, name_th, default_hours,
                   default_bonus_deduction_enabled, default_bonus_deduction_percent,
                   is_active, updated_at
            FROM public.leave_types
            ORDER BY id
            """;
        var result = new List<LeaveTypeMasterDto>();
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LeaveTypeMasterDto(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetDecimal(3), reader.GetBoolean(4), reader.GetDecimal(5),
                reader.GetBoolean(6), reader.GetFieldValue<DateTimeOffset>(7)));
        }
        return Ok(result);
    }

    [HttpPost("leave-types")]
    public Task<ActionResult<LeaveTypeMasterDto>> CreateLeaveType(
        SaveLeaveTypeMasterRequest request,
        CancellationToken cancellationToken) => SaveLeaveType(null, request, cancellationToken);

    [HttpPut("leave-types/{id:long}")]
    public Task<ActionResult<LeaveTypeMasterDto>> UpdateLeaveType(
        long id,
        SaveLeaveTypeMasterRequest request,
        CancellationToken cancellationToken) => SaveLeaveType(id, request, cancellationToken);

    [HttpGet("attendance-event-types")]
    public async Task<ActionResult<IReadOnlyList<AttendanceEventTypeMasterDto>>> GetAttendanceEventTypes(
        [FromQuery] bool includeInactive = true,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, code, name_th, name_en, counts_as_work_time,
                   display_order, is_active, updated_at
            FROM public.attendance_event_types
            WHERE (@include_inactive OR is_active = TRUE)
            ORDER BY display_order, name_th, id
            """;
        var result = new List<AttendanceEventTypeMasterDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("include_inactive", includeInactive);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadAttendanceEventType(reader));
        return Ok(result);
    }

    [HttpPost("attendance-event-types")]
    public Task<ActionResult<AttendanceEventTypeMasterDto>> CreateAttendanceEventType(
        SaveAttendanceEventTypeMasterRequest request,
        CancellationToken cancellationToken) => SaveAttendanceEventType(null, request, cancellationToken);

    [HttpPut("attendance-event-types/{id:long}")]
    public Task<ActionResult<AttendanceEventTypeMasterDto>> UpdateAttendanceEventType(
        long id,
        SaveAttendanceEventTypeMasterRequest request,
        CancellationToken cancellationToken) => SaveAttendanceEventType(id, request, cancellationToken);

    private async Task<ActionResult<MasterDataItemDto>> SaveItem(
        long? id,
        SaveMasterDataItemRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanSave(id, request.IsActive, cancellationToken)) return Forbid();
        var category = request.CategoryCode?.Trim().ToUpperInvariant() ?? string.Empty;
        var code = request.ItemCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!IsGenericCategory(category) || string.IsNullOrWhiteSpace(code) ||
            string.IsNullOrWhiteSpace(request.NameTh) || request.DisplayOrder < 0)
            return BadRequest("กรุณากรอกรหัส ชื่อ และลำดับให้ถูกต้อง");

        var parentCategory = ParentCategory(category);
        var parentItemId = parentCategory is null ? null : request.ParentItemId;
        if (parentCategory is not null &&
            (!parentItemId.HasValue ||
             !await IsActiveParent(parentItemId.Value, parentCategory, cancellationToken)))
            return BadRequest($"กรุณาเลือกข้อมูลแม่ประเภท {parentCategory}");

        const string insertSql = """
            INSERT INTO public.system_master_items
                (category_code, parent_item_id, item_code, name_th, name_en, display_order, is_active)
            VALUES (@category, @parent_item_id, @code, @name_th, @name_en, @display_order, @is_active)
            RETURNING id
            """;
        const string updateSql = """
            UPDATE public.system_master_items SET
                category_code = @category, item_code = @code,
                name_th = @name_th, name_en = @name_en,
                display_order = @display_order, is_active = @is_active,
                parent_item_id = @parent_item_id
            WHERE id = @id
            RETURNING id
            """;
        try
        {
            await using var command = dataSource.CreateCommand(id.HasValue ? updateSql : insertSql);
            if (id.HasValue) command.Parameters.AddWithValue("id", id.Value);
            command.Parameters.AddWithValue("category", category);
            command.Parameters.Add(new NpgsqlParameter<long?>("parent_item_id", parentItemId));
            command.Parameters.AddWithValue("code", code);
            command.Parameters.AddWithValue("name_th", request.NameTh.Trim());
            command.Parameters.Add(new NpgsqlParameter<string?>("name_en", NullIfEmpty(request.NameEn)));
            command.Parameters.AddWithValue("display_order", request.DisplayOrder);
            command.Parameters.AddWithValue("is_active", request.IsActive);
            var savedId = (long?)await command.ExecuteScalarAsync(cancellationToken);
            if (!savedId.HasValue) return NotFound();
            return Ok(await FindItem(savedId.Value, cancellationToken));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Conflict("รหัสนี้มีอยู่ในหมวดข้อมูลแล้ว");
        }
    }

    private async Task<ActionResult<LeaveTypeMasterDto>> SaveLeaveType(
        long? id,
        SaveLeaveTypeMasterRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanSave(id, request.IsActive, cancellationToken)) return Forbid();
        var code = request.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(request.NameTh) ||
            request.DefaultHours < 0 || request.DefaultBonusDeductionPercent is < 0 or > 100)
            return BadRequest("กรุณากรอกรหัส ชื่อ และจำนวนชั่วโมงเริ่มต้นให้ถูกต้อง");

        const string insertSql = """
            INSERT INTO public.leave_types
                (code, name_th, default_hours, default_bonus_deduction_enabled,
                 default_bonus_deduction_percent, is_active)
            VALUES
                (@code, @name_th, @default_hours, @bonus_enabled,
                 @bonus_percent, @is_active)
            RETURNING id
            """;
        const string updateSql = """
            UPDATE public.leave_types SET
                code = @code, name_th = @name_th,
                default_hours = @default_hours,
                default_bonus_deduction_enabled = @bonus_enabled,
                default_bonus_deduction_percent = @bonus_percent,
                is_active = @is_active
            WHERE id = @id
            RETURNING id
            """;
        try
        {
            await using var command = dataSource.CreateCommand(id.HasValue ? updateSql : insertSql);
            if (id.HasValue) command.Parameters.AddWithValue("id", id.Value);
            command.Parameters.AddWithValue("code", code);
            command.Parameters.AddWithValue("name_th", request.NameTh.Trim());
            command.Parameters.AddWithValue("default_hours", request.DefaultHours);
            command.Parameters.AddWithValue("bonus_enabled", request.DefaultBonusDeductionEnabled);
            command.Parameters.AddWithValue(
                "bonus_percent",
                request.DefaultBonusDeductionEnabled ? request.DefaultBonusDeductionPercent : 0);
            command.Parameters.AddWithValue("is_active", request.IsActive);
            var savedId = (long?)await command.ExecuteScalarAsync(cancellationToken);
            if (!savedId.HasValue) return NotFound();
            return Ok(await FindLeaveType(savedId.Value, cancellationToken));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Conflict("รหัสประเภทการลานี้มีอยู่แล้ว");
        }
    }

    private async Task<ActionResult<AttendanceEventTypeMasterDto>> SaveAttendanceEventType(
        long? id,
        SaveAttendanceEventTypeMasterRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanSave(id, request.IsActive, cancellationToken)) return Forbid();
        var code = request.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(request.NameTh) ||
            request.DisplayOrder < 0)
            return BadRequest("กรุณากรอกรหัส ชื่อ และลำดับแสดงผลให้ถูกต้อง");

        const string insertSql = """
            INSERT INTO public.attendance_event_types
                (code, name_th, name_en, counts_as_work_time, display_order, is_active)
            VALUES
                (@code, @name_th, @name_en, @counts_as_work_time, @display_order, @is_active)
            RETURNING id
            """;
        const string updateSql = """
            UPDATE public.attendance_event_types SET
                code = @code, name_th = @name_th, name_en = @name_en,
                counts_as_work_time = @counts_as_work_time,
                display_order = @display_order, is_active = @is_active
            WHERE id = @id
            RETURNING id
            """;
        try
        {
            await using var command = dataSource.CreateCommand(id.HasValue ? updateSql : insertSql);
            if (id.HasValue) command.Parameters.AddWithValue("id", id.Value);
            command.Parameters.AddWithValue("code", code);
            command.Parameters.AddWithValue("name_th", request.NameTh.Trim());
            command.Parameters.Add(new NpgsqlParameter<string?>("name_en", NullIfEmpty(request.NameEn)));
            command.Parameters.AddWithValue("counts_as_work_time", request.CountsAsWorkTime);
            command.Parameters.AddWithValue("display_order", request.DisplayOrder);
            command.Parameters.AddWithValue("is_active", request.IsActive);
            var savedId = (long?)await command.ExecuteScalarAsync(cancellationToken);
            if (!savedId.HasValue) return NotFound();
            return Ok(await FindAttendanceEventType(savedId.Value, cancellationToken));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Conflict("รหัสประเภท Event นี้มีอยู่แล้ว");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return Conflict("ไม่สามารถเปลี่ยนรหัสประเภท Event ที่ถูกใช้งานแล้วได้");
        }
    }

    private async Task<MasterDataItemDto?> FindItem(long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT item.id, item.category_code, item.item_code, item.name_th, item.name_en,
                   item.display_order, item.is_active, item.parent_item_id,
                   parent.name_th, item.updated_at
            FROM public.system_master_items item
            LEFT JOIN public.system_master_items parent ON parent.id = item.parent_item_id
            WHERE item.id = @id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new MasterDataItemDto(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5), reader.GetBoolean(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9));
    }

    private async Task<LeaveTypeMasterDto?> FindLeaveType(long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, code, name_th, default_hours,
                   default_bonus_deduction_enabled, default_bonus_deduction_percent,
                   is_active, updated_at
            FROM public.leave_types WHERE id = @id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new LeaveTypeMasterDto(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetDecimal(3), reader.GetBoolean(4), reader.GetDecimal(5),
            reader.GetBoolean(6), reader.GetFieldValue<DateTimeOffset>(7));
    }

    private async Task<AttendanceEventTypeMasterDto?> FindAttendanceEventType(
        long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, code, name_th, name_en, counts_as_work_time,
                   display_order, is_active, updated_at
            FROM public.attendance_event_types
            WHERE id = @id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAttendanceEventType(reader) : null;
    }

    private static AttendanceEventTypeMasterDto ReadAttendanceEventType(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4),
        reader.GetInt32(5), reader.GetBoolean(6), reader.GetFieldValue<DateTimeOffset>(7));

    private static bool IsGenericCategory(string? category) =>
        Categories.Any(item => !item.IsLeaveType && !item.IsAttendanceEventType &&
            string.Equals(item.Code, category?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string? ParentCategory(string category) => category switch
    {
        "DEPARTMENT" => "BUSINESS_UNIT",
        "DISTRICT" => "PROVINCE",
        "SUBDISTRICT" => "DISTRICT",
        "POSTAL_CODE" => "SUBDISTRICT",
        _ => null
    };

    private async Task<bool> IsActiveParent(long id, string category, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS
            (
                SELECT 1 FROM public.system_master_items
                WHERE id = @id AND category_code = @category AND is_active = TRUE
            )
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("category", category);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<bool> CanSave(long? id, bool isActive, CancellationToken cancellationToken)
    {
        var employeeId = await ResolveAuthenticatedEmployeeId(cancellationToken);
        var action = !id.HasValue ? "CREATE" : isActive ? "EDIT" : "DEACTIVATE";
        return !string.IsNullOrWhiteSpace(employeeId) &&
               await pageAccessService.HasAccess(employeeId, "SYSTEM_MASTER_DATA", cancellationToken) &&
               await actionPermissionService.HasPermission(employeeId, "SYSTEM_MASTER_DATA", action, cancellationToken);
    }

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
            WHERE tenant_id = @tenant_id AND entra_object_id = @object_id AND is_active = TRUE
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("object_id", objectId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}
