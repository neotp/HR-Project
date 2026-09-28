using System.Globalization;
using System.Text.Json;
using HrProject.Shared.Models;
using Npgsql;

namespace HrProject.Api.Services;

public static class LotusNotesNoShowOutboxService
{
    public static async Task QueueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long preEmployeeId,
        Employee employee,
        string reason,
        string actorName,
        DateTimeOffset confirmedAt,
        IConfiguration configuration,
        CancellationToken token)
    {
        var nationalId = employee.NationalId?.Trim() ?? string.Empty;
        if (nationalId.Length != 13 || nationalId.Any(character => !char.IsDigit(character)))
            throw new ArgumentException(
                "ต้องระบุเลขบัตรประชาชน 13 หลักก่อนยืนยันไม่มาทำงาน เพื่อใช้เป็น Key ของ Lotus Notes");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("กรุณาระบุเหตุผลที่ไม่มาทำงาน");
        if (string.IsNullOrWhiteSpace(actorName))
            throw new ArgumentException("ไม่พบชื่อผู้ยืนยันรายการไม่มาทำงาน");

        // A pre-employee may be incomplete. Only update the cancellation fields;
        // sending the entire create payload could replace valid Notes data with a draft.
        var payload = new Dictionary<string, string>
        {
            ["IdNumber"] = nationalId,
            ["CANCELSTARTREM"] = reason.Trim(),
            ["CANCELSTARTBY"] = actorName.Trim(),
            ["CANCELSTARTAT"] = confirmedAt.ToOffset(TimeSpan.FromHours(7))
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        };
        var employeeCode = employee.EmployeeCode?.Trim() ?? string.Empty;
        if (employeeCode.Length > 0)
            payload["Code_Emp"] = employeeCode;

        const string sql = """
            INSERT INTO public.lotus_notes_employee_outbox
                (pre_employee_id, employee_id, employee_code, employee_name,
                 database_name, external_key, payload, status)
            VALUES
                (@pre_employee_id, NULL, @employee_code, @employee_name,
                 @database_name, @external_key, @payload::jsonb, 'PENDING')
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("pre_employee_id", preEmployeeId);
        command.Parameters.AddWithValue("employee_code", employeeCode);
        command.Parameters.AddWithValue("employee_name", string.IsNullOrWhiteSpace(employee.ThaiFullName)
            ? $"{employee.FirstName} {employee.LastName}".Trim()
            : employee.ThaiFullName.Trim());
        command.Parameters.AddWithValue("database_name",
            Environment.GetEnvironmentVariable("LOTUS_NOTES_DATABASE") ??
            configuration["LotusNotes:Database"] ?? "Employee");
        command.Parameters.AddWithValue("external_key", nationalId);
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(payload));
        await command.ExecuteNonQueryAsync(token);
    }
}
