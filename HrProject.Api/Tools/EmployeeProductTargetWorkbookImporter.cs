using System.IO.Compression;
using System.Xml.Linq;
using HrProject.Shared.Models;
using Npgsql;
using NpgsqlTypes;

namespace HrProject.Api.Tools;

internal static class EmployeeProductTargetWorkbookImporter
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    internal sealed record Result(int SourceRows, int Employees, int MatchedEmployees,
        IReadOnlyList<string> MissingEmployeeCodes, int ProductBusinessUnits, int Brands,
        int CommGroups, int CurrentAssignments, int ExtraCurrentAssignments, bool Preview);

    internal static async Task<Result> RunAsync(string connectionString, string workbookPath,
        string periodCode, bool preview, bool verifyOnly = false, CancellationToken token = default)
    {
        if (!File.Exists(workbookPath)) throw new FileNotFoundException("ไม่พบไฟล์ Product Target", workbookPath);
        if (string.IsNullOrWhiteSpace(periodCode) || periodCode.Length > 20)
            throw new ArgumentException("Period code must be 1 to 20 characters.", nameof(periodCode));
        var rows = ReadRows(workbookPath);
        if (rows.Count == 0) throw new InvalidOperationException("ไฟล์ไม่มีรายการ Product Target");
        var duplicates = rows.GroupBy(row => (row.Code.ToUpperInvariant(), row.ProductBu.ToUpperInvariant(),
                row.Brand.ToUpperInvariant(), row.CommGroup.ToUpperInvariant()))
            .Where(group => group.Count() > 1).ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException($"พบ Product Target ซ้ำ {duplicates.Count} ชุดในไฟล์");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        try
        {
            await Execute(connection, transaction, """
                CREATE TEMP TABLE product_target_import
                (employee_code TEXT NOT NULL, product_bu TEXT NOT NULL,
                 brand TEXT NOT NULL, comm_group TEXT NOT NULL) ON COMMIT DROP
                """, token);
            await using (var command = new NpgsqlCommand("""
                INSERT INTO product_target_import(employee_code,product_bu,brand,comm_group)
                VALUES (@code,@bu,@brand,@comm)
                """, connection, transaction))
            {
                command.Parameters.Add("code", NpgsqlDbType.Text);
                command.Parameters.Add("bu", NpgsqlDbType.Text);
                command.Parameters.Add("brand", NpgsqlDbType.Text);
                command.Parameters.Add("comm", NpgsqlDbType.Text);
                await command.PrepareAsync(token);
                foreach (var row in rows)
                {
                    command.Parameters["code"].Value = row.Code;
                    command.Parameters["bu"].Value = row.ProductBu;
                    command.Parameters["brand"].Value = row.Brand;
                    command.Parameters["comm"].Value = row.CommGroup;
                    await command.ExecuteNonQueryAsync(token);
                }
            }

            var missingCodes = new List<string>();
            await using (var command = new NpgsqlCommand("""
                SELECT DISTINCT source.employee_code
                FROM product_target_import source
                WHERE NOT EXISTS
                (
                    SELECT 1 FROM public.employees employee
                    WHERE CASE WHEN employee.employee_code ~ '^[0-9]+$'
                                AND LENGTH(employee.employee_code)<6
                               THEN LPAD(employee.employee_code,6,'0')
                               ELSE employee.employee_code END = source.employee_code
                )
                ORDER BY 1
                """, connection, transaction))
            await using (var reader = await command.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) missingCodes.Add(reader.GetString(0));

            var employeeCount = rows.Select(row => row.Code).Distinct(StringComparer.Ordinal).Count();
            if ((!preview || verifyOnly) && missingCodes.Count > 0)
                throw new InvalidOperationException(
                    $"ไม่พบรหัสพนักงาน {missingCodes.Count} รหัส: {string.Join(", ", missingCodes.Take(20))}");

            if (!preview && !verifyOnly)
            {
                await UpsertMaster(connection, transaction, "product_business_units",
                    "product_business_unit_code", "product_business_unit_name", "product_bu", "PBU-", token);
                await UpsertMaster(connection, transaction, "brands",
                    "brand_code", "brand_name", "brand", "BR-", token);
                await UpsertMaster(connection, transaction, "comm_groups",
                    "comm_group_code", "comm_group_name", "comm_group", "CG-", token);

                await Execute(connection, transaction, """
                    UPDATE public.employee_product_targets target SET is_current=FALSE
                    WHERE target.employee_id IN
                    (
                        SELECT employee.id FROM public.employees employee
                        JOIN product_target_import source ON
                            CASE WHEN employee.employee_code ~ '^[0-9]+$'
                                      AND LENGTH(employee.employee_code)<6
                                 THEN LPAD(employee.employee_code,6,'0')
                                 ELSE employee.employee_code END = source.employee_code
                    )
                    """, token);
                await Execute(connection, transaction, """
                    INSERT INTO public.employee_product_targets
                        (period_code,employee_id,product_business_unit_id,brand_id,comm_group_id,
                         department_snapshot,is_current,assigned_by)
                    SELECT @period,employee.id,product_bu.id,brand.id,comm.id,
                           company.department,TRUE,'Product Target workbook import'
                    FROM product_target_import source
                    JOIN public.employees employee ON
                        CASE WHEN employee.employee_code ~ '^[0-9]+$'
                                  AND LENGTH(employee.employee_code)<6
                             THEN LPAD(employee.employee_code,6,'0')
                             ELSE employee.employee_code END = source.employee_code
                    JOIN public.product_business_units product_bu
                      ON LOWER(BTRIM(product_bu.product_business_unit_name))=LOWER(source.product_bu)
                    JOIN public.brands brand ON LOWER(BTRIM(brand.brand_name))=LOWER(source.brand)
                    JOIN public.comm_groups comm ON LOWER(BTRIM(comm.comm_group_name))=LOWER(source.comm_group)
                    LEFT JOIN public.employee_company_info company ON company.employee_id=employee.id
                    ON CONFLICT (period_code,employee_id,product_business_unit_id,brand_id,comm_group_id)
                    DO UPDATE SET is_current=TRUE,department_snapshot=EXCLUDED.department_snapshot,
                                  assigned_by=EXCLUDED.assigned_by,assigned_at=CURRENT_TIMESTAMP
                    """, token, periodCode);
            }

            var currentAssignments = preview && !verifyOnly ? 0 : await Scalar(connection, transaction, """
                SELECT COUNT(*) FROM public.employee_product_targets target
                JOIN public.employees employee ON employee.id=target.employee_id
                JOIN public.product_business_units product_bu ON product_bu.id=target.product_business_unit_id
                JOIN public.brands brand ON brand.id=target.brand_id
                JOIN public.comm_groups comm ON comm.id=target.comm_group_id
                WHERE target.period_code=@period AND target.is_current=TRUE
                  AND EXISTS (SELECT 1 FROM product_target_import source
                              WHERE source.employee_code=CASE
                                  WHEN employee.employee_code ~ '^[0-9]+$'
                                       AND LENGTH(employee.employee_code)<6
                                  THEN LPAD(employee.employee_code,6,'0')
                                  ELSE employee.employee_code END
                                AND LOWER(source.product_bu)=LOWER(product_bu.product_business_unit_name)
                                AND LOWER(source.brand)=LOWER(brand.brand_name)
                                AND LOWER(source.comm_group)=LOWER(comm.comm_group_name))
                """, token, periodCode);
            var allCurrentAssignments = preview && !verifyOnly ? 0 : await Scalar(connection, transaction, """
                SELECT COUNT(*) FROM public.employee_product_targets target
                JOIN public.employees employee ON employee.id=target.employee_id
                WHERE target.is_current=TRUE AND EXISTS
                    (SELECT 1 FROM product_target_import source
                     WHERE source.employee_code=CASE
                         WHEN employee.employee_code ~ '^[0-9]+$'
                              AND LENGTH(employee.employee_code)<6
                         THEN LPAD(employee.employee_code,6,'0')
                         ELSE employee.employee_code END)
                """, token, periodCode);
            if ((!preview || verifyOnly) &&
                (currentAssignments != rows.Count || allCurrentAssignments != rows.Count))
                throw new InvalidOperationException(
                    $"รายการปัจจุบันไม่ตรงกับไฟล์: matched={currentAssignments}, all={allCurrentAssignments}, source={rows.Count}");

            if (preview || verifyOnly) await transaction.RollbackAsync(token);
            else await transaction.CommitAsync(token);
            return new Result(rows.Count, employeeCount, employeeCount - missingCodes.Count,
                missingCodes, rows.Select(r => r.ProductBu).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                rows.Select(r => r.Brand).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                rows.Select(r => r.CommGroup).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                currentAssignments, allCurrentAssignments - currentAssignments, preview);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    private static async Task UpsertMaster(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string table, string codeColumn, string nameColumn, string sourceColumn, string codePrefix,
        CancellationToken token)
    {
        // Identifiers are fixed call-site constants; values remain query parameters.
        await Execute(connection, transaction, $"""
            INSERT INTO public.{table}({codeColumn},{nameColumn},is_active)
            SELECT CASE WHEN LENGTH(source.name)<=50 THEN source.name
                        ELSE @prefix || MD5(LOWER(source.name)) END,source.name,TRUE
            FROM (SELECT MIN({sourceColumn}) AS name FROM product_target_import
                  GROUP BY LOWER({sourceColumn})) source
            WHERE NOT EXISTS
                (SELECT 1 FROM public.{table} existing
                 WHERE LOWER(BTRIM(existing.{nameColumn}))=LOWER(source.name))
            ON CONFLICT DO NOTHING
            """, token, codePrefix);
        await Execute(connection, transaction, $"""
            UPDATE public.{table} master SET {codeColumn}=master.{nameColumn}
            WHERE master.{codeColumn}=@prefix || MD5(LOWER(master.{nameColumn}))
              AND LENGTH(master.{nameColumn})<=50
              AND EXISTS (SELECT 1 FROM product_target_import source
                          WHERE LOWER(BTRIM(master.{nameColumn}))=LOWER(source.{sourceColumn}))
              AND NOT EXISTS (SELECT 1 FROM public.{table} other
                              WHERE other.id<>master.id
                                AND LOWER(BTRIM(other.{codeColumn}))=LOWER(BTRIM(master.{nameColumn})))
            """, token, codePrefix);
        await Execute(connection, transaction, $"""
            UPDATE public.{table} master SET is_active=TRUE
            WHERE EXISTS (SELECT 1 FROM product_target_import source
                          WHERE LOWER(BTRIM(master.{nameColumn}))=LOWER(source.{sourceColumn}))
            """, token);
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken token, string? value = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (sql.Contains("@period", StringComparison.Ordinal)) command.Parameters.AddWithValue("period", value!);
        if (sql.Contains("@prefix", StringComparison.Ordinal)) command.Parameters.AddWithValue("prefix", value!);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<int> Scalar(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken token, string period)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("period", period);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    private sealed record Row(string Code, string ProductBu, string Brand, string CommGroup);

    private static List<Row> ReadRows(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var stringsEntry = archive.GetEntry("xl/sharedStrings.xml");
        var strings = new List<string>();
        if (stringsEntry is not null)
        {
            using var stream = stringsEntry.Open();
            strings = XDocument.Load(stream).Descendants(Ns + "si")
                .Select(item => string.Concat(item.Descendants(Ns + "t").Select(t => t.Value))).ToList();
        }
        using var sheetStream = (archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new InvalidOperationException("ไม่พบ worksheet แรก")).Open();
        var sheetRows = XDocument.Load(sheetStream).Descendants(Ns + "row").ToList();
        if (sheetRows.Count == 0) return [];
        Dictionary<string, string> Cells(XElement row) => row.Elements(Ns + "c").ToDictionary(
            cell => new string(((string?)cell.Attribute("r") ?? "").TakeWhile(char.IsLetter).ToArray()),
            cell => CellValue(cell, strings), StringComparer.OrdinalIgnoreCase);
        var headers = Cells(sheetRows[0]).Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Value.Trim(), pair => pair.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var header in new[] { "Code", "BU", "Brand", "Commgrp Target" })
            if (!headers.ContainsKey(header)) throw new InvalidOperationException($"ไม่พบคอลัมน์ {header}");

        var result = new List<Row>();
        foreach (var element in sheetRows.Skip(1))
        {
            var cells = Cells(element);
            string V(string header) => cells.GetValueOrDefault(headers[header], "").Trim();
            if (new[] { "Code", "BU", "Brand", "Commgrp Target" }.All(h => V(h).Length == 0)) continue;
            var code = EmployeeCodeFormat.NormalizeNew(V("Code"));
            if (!EmployeeCodeFormat.IsValid(code))
                throw new InvalidOperationException($"รหัสพนักงานไม่ถูกต้องที่แถว {element.Attribute("r")}: {V("Code")}");
            var bu = V("BU");
            var brand = V("Brand");
            var comm = V("Commgrp Target");
            if (bu.Length == 0 || brand.Length == 0 || comm.Length == 0 ||
                bu.Length > 200 || brand.Length > 200 || comm.Length > 200)
                throw new InvalidOperationException($"Product BU, Brand หรือ Comm.Group ไม่ถูกต้องที่แถว {element.Attribute("r")}");
            result.Add(new Row(code, bu, brand, comm));
        }
        return result;
    }

    private static string CellValue(XElement cell, IReadOnlyList<string> strings)
    {
        if ((string?)cell.Attribute("t") == "inlineStr")
            return string.Concat(cell.Descendants(Ns + "t").Select(t => t.Value));
        var raw = cell.Element(Ns + "v")?.Value ?? "";
        return (string?)cell.Attribute("t") == "s" && int.TryParse(raw, out var index)
            ? strings[index] : raw;
    }
}
