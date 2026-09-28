using Npgsql;

namespace HrProject.Api.Services;

internal static class WorkScheduleValueValidator
{
    internal const string InvalidMessage =
        "ประเภทพนักงานและเวลาทำงานไม่อยู่ใน Master กรุณาเลือกค่าที่มีอยู่ในระบบ";

    private const string ExistsSql = """
        SELECT EXISTS
        (
            SELECT 1
            FROM public.system_master_items item
            LEFT JOIN public.work_schedule_aliases alias
              ON alias.master_item_id = item.id
             AND LOWER(alias.alias_value) = LOWER(BTRIM(@value))
            WHERE item.category_code = 'WORK_SCHEDULE'
              AND (LOWER(item.item_code) = LOWER(BTRIM(@value))
                   OR LOWER(item.name_th) = LOWER(BTRIM(@value))
                   OR alias.master_item_id IS NOT NULL)
        )
        """;

    internal static async Task<bool> IsKnownAsync(
        NpgsqlDataSource dataSource, string? value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        await using var command = dataSource.CreateCommand(ExistsSql);
        command.Parameters.AddWithValue("value", value.Trim());
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    internal static async Task<bool> IsKnownAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string? value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        await using var command = new NpgsqlCommand(ExistsSql, connection, transaction);
        command.Parameters.AddWithValue("value", value.Trim());
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
