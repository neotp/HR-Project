using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Npgsql;

namespace HrProject.Api.Tools;

internal static class EmployeeWorkbookImporter
{
    internal sealed record VerificationResult(
        long Employees,
        long BasicInfo,
        long CompanyInfo,
        long PersonalInfo,
        long FamilyInfo);

    internal sealed record ImportResult(int Inserted, int Skipped);

    private static readonly XNamespace SpreadsheetNs =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    internal static async Task<ImportResult> ImportAsync(
        string connectionString,
        string workbookPath,
        string migrationPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(workbookPath))
            throw new FileNotFoundException("ไม่พบไฟล์ Excel", workbookPath);
        if (!File.Exists(migrationPath))
            throw new FileNotFoundException("ไม่พบ migration Employee", migrationPath);

        var rows = ReadRows(workbookPath);
        if (rows.Count == 0)
            throw new InvalidOperationException("ไฟล์ Excel ไม่มีข้อมูลพนักงาน");
        if (rows.Select(row => row.EmployeeCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count)
            throw new InvalidOperationException("พบรหัสพนักงานซ้ำในไฟล์ Excel");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var migration = new NpgsqlCommand(
            await File.ReadAllTextAsync(migrationPath, cancellationToken), connection))
        {
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var inserted = 0;
            var skipped = 0;
            foreach (var row in rows)
            {
                var employeeId = await InsertEmployeeIfMissing(connection, transaction, row, cancellationToken);
                if (employeeId is null)
                {
                    skipped++;
                    continue;
                }

                await UpsertBasicInfo(connection, transaction, employeeId.Value, row, cancellationToken);
                await UpsertPersonalInfo(connection, transaction, employeeId.Value, row, cancellationToken);
                await UpsertCompanyInfo(connection, transaction, employeeId.Value, row, cancellationToken);
                await EnsureEmptyFamilyRow(connection, transaction, employeeId.Value, cancellationToken);
                inserted++;
            }

            await transaction.CommitAsync(cancellationToken);
            return new ImportResult(inserted, skipped);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    internal static async Task<VerificationResult> VerifyAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM public.employees),
                (SELECT COUNT(*) FROM public.employee_basic_info),
                (SELECT COUNT(*) FROM public.employee_company_info),
                (SELECT COUNT(*) FROM public.employee_personal_info),
                (SELECT COUNT(*) FROM public.employee_family_info)
            """;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new VerificationResult(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4));
    }

    private static List<EmployeeImportRow> ReadRows(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var sharedStrings = ReadSharedStrings(archive);
        var sheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new InvalidOperationException("ไม่พบ worksheet แรกในไฟล์ Excel");
        using var stream = sheetEntry.Open();
        var document = XDocument.Load(stream);
        var result = new List<EmployeeImportRow>();

        foreach (var rowElement in document.Descendants(SpreadsheetNs + "row"))
        {
            var rowNumber = (int?)rowElement.Attribute("r") ?? 0;
            if (rowNumber <= 1)
                continue;

            var cells = rowElement.Elements(SpreadsheetNs + "c")
                .ToDictionary(
                    cell => ColumnName((string?)cell.Attribute("r") ?? string.Empty),
                    cell => CellValue(cell, sharedStrings),
                    StringComparer.OrdinalIgnoreCase);
            var code = Value(cells, "A");
            if (string.IsNullOrWhiteSpace(code))
                continue;

            var thaiName = Value(cells, "C");
            var englishName = Value(cells, "D");
            var (firstNameTh, lastNameTh) = SplitName(thaiName);
            var (firstNameEn, lastNameEn) = SplitName(englishName);
            result.Add(new EmployeeImportRow(
                rowNumber,
                Path.GetFileName(path),
                code,
                Value(cells, "B"),
                firstNameTh,
                lastNameTh,
                thaiName,
                firstNameEn,
                lastNameEn,
                englishName,
                Value(cells, "E"),
                Value(cells, "F"),
                Value(cells, "G"),
                Value(cells, "H"),
                Value(cells, "I"),
                Value(cells, "J"),
                Value(cells, "K"),
                Value(cells, "L"),
                Value(cells, "M"),
                Value(cells, "N"),
                ParseExcelDate(Value(cells, "O")),
                ParseExcelDate(Value(cells, "P")),
                Value(cells, "Q"),
                Value(cells, "R")));
        }

        return result;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
            return [];
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        return document.Descendants(SpreadsheetNs + "si")
            .Select(item => string.Concat(item.Descendants(SpreadsheetNs + "t").Select(text => text.Value)))
            .ToList();
    }

    private static string CellValue(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = (string?)cell.Attribute("t");
        if (type == "inlineStr")
            return string.Concat(cell.Descendants(SpreadsheetNs + "t").Select(text => text.Value)).Trim();
        var raw = cell.Element(SpreadsheetNs + "v")?.Value ?? string.Empty;
        if (type == "s" && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return index >= 0 && index < sharedStrings.Count ? sharedStrings[index].Trim() : string.Empty;
        return raw.Trim();
    }

    private static string ColumnName(string reference) =>
        new(reference.TakeWhile(char.IsLetter).ToArray());

    private static string Value(IReadOnlyDictionary<string, string> values, string column) =>
        values.TryGetValue(column, out var value) ? value.Trim() : string.Empty;

    private static (string FirstName, string LastName) SplitName(string fullName)
    {
        var parts = fullName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => (string.Empty, string.Empty),
            1 => (parts[0], string.Empty),
            _ => (parts[0], parts[1])
        };
    }

    private static DateOnly? ParseExcelDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        if (DateOnly.TryParse(value, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var date))
            return date;
        return null;
    }

    private static async Task<long?> InsertEmployeeIfMissing(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        EmployeeImportRow row, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.employees
                (employee_code, is_active, source_system, source_row)
            VALUES (@code, TRUE, @source_system, @source_row)
            ON CONFLICT (employee_code) DO NOTHING
            RETURNING id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("code", row.EmployeeCode);
        command.Parameters.AddWithValue("source_system", row.SourceSystem);
        command.Parameters.AddWithValue("source_row", row.SourceRow);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long id ? id : null;
    }

    private static async Task UpsertBasicInfo(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long employeeId, EmployeeImportRow row, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.employee_basic_info
                (employee_id, title, first_name_th, last_name_th, full_name_th,
                 first_name_en, last_name_en, full_name_en, nickname, email_alias,
                 personal_mobile, home_phone)
            VALUES
                (@id, @title, @first_th, @last_th, @full_th,
                 @first_en, @last_en, @full_en, @nickname, @email_alias,
                 @personal_mobile, @home_phone)
            ON CONFLICT (employee_id) DO UPDATE SET
                title = EXCLUDED.title,
                first_name_th = EXCLUDED.first_name_th,
                last_name_th = EXCLUDED.last_name_th,
                full_name_th = EXCLUDED.full_name_th,
                first_name_en = EXCLUDED.first_name_en,
                last_name_en = EXCLUDED.last_name_en,
                full_name_en = EXCLUDED.full_name_en,
                nickname = EXCLUDED.nickname,
                email_alias = EXCLUDED.email_alias,
                personal_mobile = EXCLUDED.personal_mobile,
                home_phone = EXCLUDED.home_phone
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", employeeId);
        AddNullable(command, "title", row.Title);
        AddNullable(command, "first_th", row.FirstNameTh);
        AddNullable(command, "last_th", row.LastNameTh);
        AddNullable(command, "full_th", row.FullNameTh);
        AddNullable(command, "first_en", row.FirstNameEn);
        AddNullable(command, "last_en", row.LastNameEn);
        AddNullable(command, "full_en", row.FullNameEn);
        AddNullable(command, "nickname", row.Nickname);
        AddNullable(command, "email_alias", row.EmailAlias);
        AddNullable(command, "personal_mobile", row.PersonalMobile);
        AddNullable(command, "home_phone", row.HomePhone);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertPersonalInfo(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long employeeId, EmployeeImportRow row, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.employee_personal_info
                (employee_id, birth_date, current_address, id_card_address)
            VALUES (@id, @birth_date, @current_address, @id_card_address)
            ON CONFLICT (employee_id) DO UPDATE SET
                birth_date = EXCLUDED.birth_date,
                current_address = EXCLUDED.current_address,
                id_card_address = EXCLUDED.id_card_address
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", employeeId);
        AddNullable(command, "birth_date", row.BirthDate);
        AddNullable(command, "current_address", row.CurrentAddress);
        AddNullable(command, "id_card_address", row.IdCardAddress);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertCompanyInfo(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long employeeId, EmployeeImportRow row, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.employee_company_info
                (employee_id, company_code, business_unit, department,
                 position_name, supervisor_name, leave_approver_name,
                 internal_extension, start_date, employee_status)
            VALUES
                (@id, @company_code, @business_unit, @department,
                 @position, @supervisor, @supervisor,
                 @extension, @start_date, 'ปฏิบัติงาน')
            ON CONFLICT (employee_id) DO UPDATE SET
                company_code = EXCLUDED.company_code,
                business_unit = EXCLUDED.business_unit,
                department = EXCLUDED.department,
                position_name = EXCLUDED.position_name,
                supervisor_name = EXCLUDED.supervisor_name,
                leave_approver_name = EXCLUDED.leave_approver_name,
                internal_extension = EXCLUDED.internal_extension,
                start_date = EXCLUDED.start_date,
                employee_status = EXCLUDED.employee_status
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", employeeId);
        AddNullable(command, "company_code", row.CompanyCode);
        AddNullable(command, "business_unit", row.BusinessUnit);
        AddNullable(command, "department", row.Department);
        AddNullable(command, "position", row.Position);
        AddNullable(command, "supervisor", row.SupervisorName);
        AddNullable(command, "extension", row.InternalExtension);
        AddNullable(command, "start_date", row.StartDate);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureEmptyFamilyRow(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long employeeId, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.employee_family_info (employee_id)
            VALUES (@id) ON CONFLICT (employee_id) DO NOTHING;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", employeeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddNullable(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(new NpgsqlParameter<string?>(name,
            string.IsNullOrWhiteSpace(value) ? null : value.Trim()));

    private static void AddNullable(NpgsqlCommand command, string name, DateOnly? value) =>
        command.Parameters.Add(new NpgsqlParameter<DateOnly?>(name, value));

    private sealed record EmployeeImportRow(
        int SourceRow,
        string SourceSystem,
        string EmployeeCode,
        string Title,
        string FirstNameTh,
        string LastNameTh,
        string FullNameTh,
        string FirstNameEn,
        string LastNameEn,
        string FullNameEn,
        string EmailAlias,
        string InternalExtension,
        string Nickname,
        string CurrentAddress,
        string IdCardAddress,
        string HomePhone,
        string PersonalMobile,
        string Position,
        string SupervisorName,
        string Department,
        DateOnly? StartDate,
        DateOnly? BirthDate,
        string CompanyCode,
        string BusinessUnit);
}
