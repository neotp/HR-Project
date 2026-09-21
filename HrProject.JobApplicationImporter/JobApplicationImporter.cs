using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ExcelDataReader;
using HrProject.Shared.Models;
using Npgsql;
using NpgsqlTypes;

internal static class JobApplicationImporter
{
    private const string SourceSystem = "JOB_APPLICATION";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal sealed record ImportResult(int TotalRows, int Inserted, int Skipped, int Invalid);

    internal static async Task<ImportResult> ImportAsync(
        string connectionString,
        string filePath,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("ไม่พบไฟล์ Job Application", filePath);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var rows = ReadRows(filePath);
        if (rows.Count == 0)
            throw new InvalidOperationException("ไฟล์ Job Application ไม่มีข้อมูลสำหรับนำเข้า");
        if (dryRun)
            return new ImportResult(rows.Count, 0, 0, rows.Count(row => string.IsNullOrWhiteSpace(row.SourceReferenceId)));

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var inserted = 0;
            var skipped = 0;
            var invalid = 0;
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.SourceReferenceId))
                {
                    invalid++;
                    continue;
                }

                if (await InsertIfMissing(connection, transaction, row, cancellationToken))
                    inserted++;
                else
                    skipped++;
            }

            await transaction.CommitAsync(cancellationToken);
            return new ImportResult(rows.Count, inserted, skipped, invalid);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static List<ImportRow> ReadRows(string filePath)
    {
        using var input = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(input, new ExcelReaderConfiguration
        {
            FallbackEncoding = Encoding.GetEncoding(874),
            LeaveOpen = false
        });

        if (!reader.Read())
            return [];

        var headers = Enumerable.Range(0, reader.FieldCount)
            .Select(index => (Name: NormalizeHeader(Value(reader, index)), Index: index))
            .Where(header => !string.IsNullOrWhiteSpace(header.Name))
            .GroupBy(header => header.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);
        EnsureRequiredHeaders(headers, ["Name Thai", "Name Eng", "เลขที่ใบสมัคร"]);

        var rows = new List<ImportRow>();
        var rowNumber = 1;
        while (reader.Read())
        {
            rowNumber++;
            var sourceReferenceId = Cell(reader, headers, "เลขที่ใบสมัคร");
            var thaiFullName = Cell(reader, headers, "Name Thai");
            var englishFullName = Cell(reader, headers, "Name Eng");
            if (string.IsNullOrWhiteSpace(sourceReferenceId) &&
                string.IsNullOrWhiteSpace(thaiFullName) &&
                string.IsNullOrWhiteSpace(englishFullName))
                continue;

            var (title, firstName, lastName) = SplitThaiName(thaiFullName);
            var periods = Cell(reader, headers, "ระยะเวลา เดือน/ปี ถึง เดือน/ปี");
            var positions = Cell(reader, headers, "ตำแหน่ง/หน้าที่");
            var companies = Cell(reader, headers, "บริษัทฯ ที่ทำงาน");
            var employee = new Employee
            {
                EmployeeCode = string.Empty,
                Title = title,
                FirstName = firstName,
                LastName = lastName,
                ThaiFullName = thaiFullName,
                EnglishFullName = englishFullName,
                Nickname = Cell(reader, headers, "ชื่อเล่น"),
                CurrentAddress = Cell(reader, headers, "ที่อยู่ปัจจุบัน"),
                NationalId = Cell(reader, headers, "เลขบัตรประชาชน"),
                BirthDate = Date(reader, headers, "วันเกิด"),
                StartDate = Date(reader, headers, "วันเริ่มงาน") ?? default,
                SupervisorName = Cell(reader, headers, "Boss"),
                Position = Cell(reader, headers, "ตำแหน่ง"),
                Department = Cell(reader, headers, "แผนก"),
                MaritalStatus = Cell(reader, headers, "สถานภาพ"),
                EmergencyContactName = Cell(reader, headers, "ผู้ติดต่อฉุกเฉิน"),
                EmergencyContactAddress = Cell(reader, headers, "ที่อยู่ผู้ติดต่อ"),
                EmergencyContactPhone = NormalizePhone(Cell(reader, headers, "โทรผู้ติดต่อ")),
                PreviousWorkPeriod = periods,
                PreviousPosition = positions,
                PreviousCompany = companies,
                WorkHistory = BuildWorkHistory(periods, positions, companies),
                EmployeeStatus = "พนักงาน"
            };

            var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceFile"] = Path.GetFileName(filePath),
                ["sourceRow"] = rowNumber
            };
            foreach (var header in headers)
                payload[header.Key] = RawValue(reader, header.Value);

            rows.Add(new ImportRow(
                sourceReferenceId.Trim(),
                employee,
                JsonSerializer.Serialize(payload, JsonOptions)));
        }

        return rows;
    }

    private static async Task<bool> InsertIfMissing(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImportRow row,
        CancellationToken cancellationToken)
    {
        var validation = ValidationMessage(row.Employee);
        const string sql = """
            INSERT INTO public.pre_employees
                (source_system, source_reference_id, source_payload, employee_code, title,
                 first_name_th, last_name_th, full_name_en, nickname, email_address,
                 personal_mobile, company_name, business_unit, department, position_name,
                 start_date, supervisor_name, leave_approver_name, employment_type,
                 work_location, status, validation_message, imported_by, imported_by_name,
                 imported_at, created_by, created_by_name, employee_data)
            VALUES
                (@source_system, @source_reference_id, @source_payload, NULL, @title,
                 @first_name, @last_name, @full_name_en, @nickname, NULL,
                 NULL, NULL, NULL, @department, @position,
                 @start_date, @supervisor, NULL, NULL,
                 NULL, 'INCOMPLETE', @validation, 'SYSTEM', 'Job Application Import',
                 CURRENT_TIMESTAMP, 'SYSTEM', 'Job Application Import', @employee_data)
            ON CONFLICT (source_system, source_reference_id)
                WHERE source_system IS NOT NULL AND source_reference_id IS NOT NULL
            DO NOTHING
            RETURNING id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddText(command, "source_system", SourceSystem);
        AddText(command, "source_reference_id", row.SourceReferenceId);
        command.Parameters.Add("source_payload", NpgsqlDbType.Jsonb).Value = row.SourcePayload;
        AddText(command, "title", row.Employee.Title);
        AddText(command, "first_name", row.Employee.FirstName);
        AddText(command, "last_name", row.Employee.LastName);
        AddText(command, "full_name_en", row.Employee.EnglishFullName);
        AddText(command, "nickname", row.Employee.Nickname);
        AddText(command, "department", row.Employee.Department);
        AddText(command, "position", row.Employee.Position);
        AddDate(command, "start_date", row.Employee.StartDate == default ? null : row.Employee.StartDate);
        AddText(command, "supervisor", row.Employee.SupervisorName);
        AddText(command, "validation", validation);
        command.Parameters.Add("employee_data", NpgsqlDbType.Jsonb).Value =
            JsonSerializer.Serialize(row.Employee, JsonOptions);
        return await command.ExecuteScalarAsync(cancellationToken) is long;
    }

    private static string ValidationMessage(Employee employee)
    {
        var missing = new List<string> { "รหัสพนักงาน", "อีเมล" };
        if (string.IsNullOrWhiteSpace(employee.FirstName)) missing.Add("ชื่อ");
        if (string.IsNullOrWhiteSpace(employee.LastName)) missing.Add("นามสกุล");
        if (string.IsNullOrWhiteSpace(employee.Department)) missing.Add("แผนก");
        if (string.IsNullOrWhiteSpace(employee.Position)) missing.Add("ตำแหน่ง");
        return $"ข้อมูลไม่ครบ: {string.Join(", ", missing)}";
    }

    private static List<EmployeeWorkHistoryItem> BuildWorkHistory(string periods, string positions, string companies)
    {
        var periodItems = SplitList(periods);
        var positionItems = SplitList(positions);
        var companyItems = SplitList(companies);
        if (periodItems.Count == 0 && positionItems.Count == 0 && companyItems.Count == 0)
            return [];
        if (periodItems.Count != positionItems.Count || periodItems.Count != companyItems.Count)
        {
            return [new EmployeeWorkHistoryItem
            {
                Period = periods,
                Position = positions,
                Company = companies
            }];
        }

        return Enumerable.Range(0, periodItems.Count)
            .Select(index => new EmployeeWorkHistoryItem
            {
                Period = periodItems[index],
                Position = positionItems[index],
                Company = companyItems[index]
            })
            .ToList();
    }

    private static List<string> SplitList(string value) => value
        .Split(["\r\n", "\n", "|"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    private static (string Title, string FirstName, string LastName) SplitThaiName(string fullName)
    {
        var value = fullName.Trim();
        var titles = new[] { "นางสาว", "นาย", "นาง", "ว่าที่ร้อยตรี", "ว่าที่ ร.ต.", "ดร." };
        var title = titles.FirstOrDefault(candidate => value.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        if (title.Length > 0)
            value = value[title.Length..].Trim();
        var parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => (title, string.Empty, string.Empty),
            1 => (title, parts[0], string.Empty),
            _ => (title, parts[0], parts[1])
        };
    }

    private static DateOnly? Date(IExcelDataReader reader, IReadOnlyDictionary<string, int> headers, string name)
    {
        if (!headers.TryGetValue(NormalizeHeader(name), out var index))
            return null;
        var value = reader.GetValue(index);
        if (value is DateTime dateTime)
            return DateOnly.FromDateTime(dateTime);
        if (value is double serial)
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        if (DateOnly.TryParseExact(text, ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;
        return null;
    }

    private static string Cell(IExcelDataReader reader, IReadOnlyDictionary<string, int> headers, string name) =>
        headers.TryGetValue(NormalizeHeader(name), out var index) ? Value(reader, index) : string.Empty;

    private static string Value(IExcelDataReader reader, int index) =>
        Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

    private static object? RawValue(IExcelDataReader reader, int index)
    {
        var value = reader.GetValue(index);
        return value is DateTime date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : value;
    }

    private static string NormalizeHeader(string value) =>
        string.Join(' ', value.Replace('\t', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static void EnsureRequiredHeaders(IReadOnlyDictionary<string, int> headers, IEnumerable<string> required)
    {
        var missing = required.Where(name => !headers.ContainsKey(NormalizeHeader(name))).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"ไม่พบคอลัมน์ที่จำเป็น: {string.Join(", ", missing)}");
    }

    private static string NormalizePhone(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length == 9 ? $"0{digits}" : value.Trim();
    }

    private static void AddText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value =
            string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static void AddDate(NpgsqlCommand command, string name, DateOnly? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Date).Value = value.HasValue ? value.Value : DBNull.Value;

    private sealed record ImportRow(
        string SourceReferenceId,
        Employee Employee,
        string SourcePayload);
}
