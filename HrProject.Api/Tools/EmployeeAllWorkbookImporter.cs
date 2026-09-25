using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using HrProject.Api.Services;
using HrProject.Shared.Models;
using Npgsql;
using NpgsqlTypes;

namespace HrProject.Api.Tools;

internal static class EmployeeAllWorkbookImporter
{
    internal sealed record ImportResult(
        int SourceRows,
        int InsertedEmployees,
        int UpdatedEmployees,
        int BusinessUnits,
        int Departments,
        int Positions,
        int LinkedSupervisors,
        int MissingOrganizationRows);

    internal sealed record VerificationResult(
        int SourceRows,
        int MatchedEmployees,
        int DuplicateCanonicalCodes,
        int EmployeeDataMismatches,
        int MissingMasterPaths,
        int InvalidSupervisorLinks,
        int TotalEmployees);

    internal sealed record MasterCleanupResult(
        bool IsPreview,
        int DeletedPositions,
        int DeletedDepartments,
        int DeletedBusinessUnits,
        int RemainingPositions,
        int RemainingDepartments,
        int RemainingBusinessUnits);

    private static readonly XNamespace SpreadsheetNs =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    internal static async Task<ImportResult> ImportAsync(
        string connectionString,
        string workbookPath,
        bool removePreviousImport = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(workbookPath))
            throw new FileNotFoundException("ไม่พบไฟล์ EmployeeAll.xlsx", workbookPath);

        var rows = ReadRows(workbookPath);
        if (rows.Count == 0)
            throw new InvalidOperationException("ไฟล์ไม่มีข้อมูลพนักงาน");
        if (rows.Select(row => row.EmployeeCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count)
            throw new InvalidOperationException("พบรหัสพนักงานซ้ำในไฟล์");

        ResolveSupervisorCodes(rows);

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (removePreviousImport)
            {
                await using var cleanup = new NpgsqlCommand("""
                    DELETE FROM public.employees
                    WHERE source_system='EmployeeAll.xlsx'
                    """, connection, transaction);
                await cleanup.ExecuteNonQueryAsync(cancellationToken);
            }

            await CreateImportTable(connection, transaction, cancellationToken);
            await InsertImportRows(connection, transaction, rows, cancellationToken);

            var existing = await ScalarInt(connection, transaction, """
                SELECT COUNT(*) FROM employee_all_import source
                JOIN public.employees employee ON CASE
                    WHEN employee.employee_code ~ '^[0-9]+$' AND LENGTH(employee.employee_code)<6
                        THEN LPAD(employee.employee_code,6,'0')
                    ELSE employee.employee_code
                END=source.employee_code
                """, cancellationToken);

            await UpsertMasters(connection, transaction, cancellationToken);
            await UpsertEmployees(connection, transaction, cancellationToken);
            await ResolveDatabaseSupervisorCodes(connection, transaction, cancellationToken);
            await UpsertEmployeeDetails(connection, transaction, cancellationToken);

            var newRows = new List<(string EmployeeCode, DateOnly StartDate)>();
            await using (var command = new NpgsqlCommand("""
                SELECT source.employee_code, source.start_date
                FROM employee_all_import source
                WHERE source.was_existing=FALSE
                """, connection, transaction))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    newRows.Add((reader.GetString(0), reader.IsDBNull(1) ? default : reader.GetFieldValue<DateOnly>(1)));
            }

            foreach (var row in newRows)
            {
                var bangkokTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
                    OperatingSystem.IsWindows() ? "SE Asia Standard Time" : "Asia/Bangkok");
                var currentYear = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, bangkokTimeZone).Year;
                var quotaStartDate = row.StartDate == default || row.StartDate.Year < currentYear
                    ? new DateOnly(currentYear, 1, 1)
                    : row.StartDate;
                await InitialLeaveQuotaService.CreateForNewEmployee(
                    connection, transaction, row.EmployeeCode, quotaStartDate,
                    "EMPLOYEE_ALL_IMPORT", "EmployeeAll.xlsx", cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            var validOrganizationRows = rows.Where(row =>
                !string.IsNullOrWhiteSpace(row.BusinessUnit) &&
                !string.IsNullOrWhiteSpace(row.Department)).ToList();
            return new ImportResult(
                rows.Count,
                rows.Count - existing,
                existing,
                validOrganizationRows.Select(row => row.BusinessUnit).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                validOrganizationRows.Select(row => $"{row.BusinessUnit}\u001f{row.Department}").Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                validOrganizationRows.Where(row => !string.IsNullOrWhiteSpace(row.Position))
                    .Select(row => $"{row.BusinessUnit}\u001f{row.Department}\u001f{row.Position}")
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                rows.Count(row => !string.IsNullOrWhiteSpace(row.SupervisorEmployeeCode)),
                rows.Count - validOrganizationRows.Count);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    internal static async Task<VerificationResult> VerifyAsync(
        string connectionString, string workbookPath, CancellationToken cancellationToken = default)
    {
        var rows = ReadRows(workbookPath);
        ResolveSupervisorCodes(rows);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await CreateImportTable(connection, transaction, cancellationToken);
        await InsertImportRows(connection, transaction, rows, cancellationToken);

        var canonicalCode = """
            CASE WHEN employee.employee_code ~ '^[0-9]+$' AND LENGTH(employee.employee_code)<6
                 THEN LPAD(employee.employee_code,6,'0') ELSE employee.employee_code END
            """;
        var matched = await ScalarInt(connection, transaction,
            $"SELECT COUNT(*) FROM employee_all_import source JOIN public.employees employee ON {canonicalCode}=source.employee_code",
            cancellationToken);
        var duplicates = await ScalarInt(connection, transaction, $"""
            SELECT COUNT(*) FROM
            (SELECT {canonicalCode} code FROM public.employees employee GROUP BY 1 HAVING COUNT(*)>1) duplicate
            """, cancellationToken);
        var mismatches = await ScalarInt(connection, transaction, $"""
            SELECT COUNT(*) FROM employee_all_import source
            JOIN public.employees employee ON {canonicalCode}=source.employee_code
            JOIN public.employee_basic_info basic ON basic.employee_id=employee.id
            JOIN public.employee_company_info company ON company.employee_id=employee.id
            WHERE COALESCE(basic.title,'')<>COALESCE(source.title,'')
               OR COALESCE(basic.first_name_th,'')<>COALESCE(source.first_name_th,'')
               OR COALESCE(basic.last_name_th,'')<>COALESCE(source.last_name_th,'')
               OR COALESCE(basic.first_name_en,'')<>COALESCE(source.first_name_en,'')
               OR COALESCE(basic.last_name_en,'')<>COALESCE(source.last_name_en,'')
               OR COALESCE(company.business_unit,'')<>COALESCE(source.business_unit,'')
               OR COALESCE(company.department,'')<>COALESCE(source.department,'')
               OR COALESCE(company.position_name,'')<>COALESCE(source.position_name,'')
               OR company.start_date IS DISTINCT FROM source.start_date
            """, cancellationToken);
        var missingMasters = await ScalarInt(connection, transaction, """
            SELECT COUNT(*) FROM
            (
                SELECT DISTINCT source.business_unit,source.department,source.position_name
                FROM employee_all_import source
                LEFT JOIN public.system_master_items bu ON bu.category_code='BUSINESS_UNIT'
                 AND bu.parent_item_id IS NULL AND bu.item_code=source.business_unit AND bu.is_active=TRUE
                LEFT JOIN public.system_master_items department ON department.category_code='DEPARTMENT'
                 AND department.parent_item_id=bu.id AND department.item_code=source.department AND department.is_active=TRUE
                LEFT JOIN public.system_master_items position ON position.category_code='POSITION'
                 AND position.parent_item_id=department.id AND position.item_code=source.position_name AND position.is_active=TRUE
                WHERE COALESCE(source.business_unit,'')<>'' AND COALESCE(source.department,'')<>''
                  AND COALESCE(source.position_name,'')<>''
                  AND (bu.id IS NULL OR department.id IS NULL OR position.id IS NULL)
            ) missing
            """, cancellationToken);
        var invalidSupervisors = await ScalarInt(connection, transaction, """
            SELECT COUNT(*) FROM employee_all_import source
            WHERE COALESCE(source.supervisor_name,'')<>''
              AND NOT EXISTS
              (
                  SELECT 1 FROM public.employees employee
                  WHERE CASE WHEN employee.employee_code ~ '^[0-9]+$' AND LENGTH(employee.employee_code)<6
                             THEN LPAD(employee.employee_code,6,'0') ELSE employee.employee_code END
                        =source.supervisor_employee_code
              )
            """, cancellationToken);
        var total = await ScalarInt(connection, transaction,
            "SELECT COUNT(*) FROM public.employees", cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return new VerificationResult(rows.Count, matched, duplicates, mismatches,
            missingMasters, invalidSupervisors, total);
    }

    internal static async Task<MasterCleanupResult> CleanupMastersAsync(
        string connectionString,
        string workbookPath,
        bool previewOnly,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(workbookPath))
            throw new FileNotFoundException("EmployeeAll.xlsx was not found.", workbookPath);

        var rows = ReadRows(workbookPath);
        if (rows.Count == 0)
            throw new InvalidOperationException("EmployeeAll.xlsx contains no employee rows.");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await CreateImportTable(connection, transaction, cancellationToken);
            await InsertImportRows(connection, transaction, rows, cancellationToken);

            var obsoletePositions = await ScalarInt(connection, transaction, """
                SELECT COUNT(*)
                FROM public.system_master_items position
                WHERE position.category_code='POSITION'
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM employee_all_import source
                      JOIN public.system_master_items department
                        ON department.id=position.parent_item_id
                       AND department.category_code='DEPARTMENT'
                      JOIN public.system_master_items bu
                        ON bu.id=department.parent_item_id
                       AND bu.category_code='BUSINESS_UNIT'
                       AND bu.parent_item_id IS NULL
                      WHERE COALESCE(BTRIM(source.business_unit),'')<>''
                        AND COALESCE(BTRIM(source.department),'')<>''
                        AND COALESCE(BTRIM(source.position_name),'')<>''
                        AND source.business_unit=bu.item_code
                        AND source.department=department.item_code
                        AND source.position_name=position.item_code
                  )
                """, cancellationToken);
            var obsoleteDepartments = await ScalarInt(connection, transaction, """
                SELECT COUNT(*)
                FROM public.system_master_items department
                WHERE department.category_code='DEPARTMENT'
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM employee_all_import source
                      JOIN public.system_master_items bu
                        ON bu.id=department.parent_item_id
                       AND bu.category_code='BUSINESS_UNIT'
                       AND bu.parent_item_id IS NULL
                      WHERE COALESCE(BTRIM(source.business_unit),'')<>''
                        AND COALESCE(BTRIM(source.department),'')<>''
                        AND source.business_unit=bu.item_code
                        AND source.department=department.item_code
                  )
                """, cancellationToken);
            var obsoleteBusinessUnits = await ScalarInt(connection, transaction, """
                SELECT COUNT(*)
                FROM public.system_master_items bu
                WHERE bu.category_code='BUSINESS_UNIT'
                  AND NOT EXISTS
                  (
                      SELECT 1 FROM employee_all_import source
                      WHERE COALESCE(BTRIM(source.business_unit),'')<>''
                        AND source.business_unit=bu.item_code
                  )
                """, cancellationToken);

            if (!previewOnly)
            {
                await ExecuteNonQuery(connection, transaction, """
                    DELETE FROM public.system_master_items position
                    WHERE position.category_code='POSITION'
                      AND NOT EXISTS
                      (
                          SELECT 1
                          FROM employee_all_import source
                          JOIN public.system_master_items department
                            ON department.id=position.parent_item_id
                           AND department.category_code='DEPARTMENT'
                          JOIN public.system_master_items bu
                            ON bu.id=department.parent_item_id
                           AND bu.category_code='BUSINESS_UNIT'
                           AND bu.parent_item_id IS NULL
                          WHERE COALESCE(BTRIM(source.business_unit),'')<>''
                            AND COALESCE(BTRIM(source.department),'')<>''
                            AND COALESCE(BTRIM(source.position_name),'')<>''
                            AND source.business_unit=bu.item_code
                            AND source.department=department.item_code
                            AND source.position_name=position.item_code
                      )
                    """, cancellationToken);
                await ExecuteNonQuery(connection, transaction, """
                    DELETE FROM public.system_master_items department
                    WHERE department.category_code='DEPARTMENT'
                      AND NOT EXISTS
                      (
                          SELECT 1
                          FROM employee_all_import source
                          JOIN public.system_master_items bu
                            ON bu.id=department.parent_item_id
                           AND bu.category_code='BUSINESS_UNIT'
                           AND bu.parent_item_id IS NULL
                          WHERE COALESCE(BTRIM(source.business_unit),'')<>''
                            AND COALESCE(BTRIM(source.department),'')<>''
                            AND source.business_unit=bu.item_code
                            AND source.department=department.item_code
                      )
                    """, cancellationToken);
                await ExecuteNonQuery(connection, transaction, """
                    DELETE FROM public.system_master_items bu
                    WHERE bu.category_code='BUSINESS_UNIT'
                      AND NOT EXISTS
                      (
                          SELECT 1 FROM employee_all_import source
                          WHERE COALESCE(BTRIM(source.business_unit),'')<>''
                            AND source.business_unit=bu.item_code
                      )
                    """, cancellationToken);
            }

            var remainingPositions = await ScalarInt(connection, transaction,
                "SELECT COUNT(*) FROM public.system_master_items WHERE category_code='POSITION'", cancellationToken);
            var remainingDepartments = await ScalarInt(connection, transaction,
                "SELECT COUNT(*) FROM public.system_master_items WHERE category_code='DEPARTMENT'", cancellationToken);
            var remainingBusinessUnits = await ScalarInt(connection, transaction,
                "SELECT COUNT(*) FROM public.system_master_items WHERE category_code='BUSINESS_UNIT'", cancellationToken);

            if (previewOnly)
                await transaction.RollbackAsync(cancellationToken);
            else
                await transaction.CommitAsync(cancellationToken);

            return new MasterCleanupResult(
                previewOnly,
                obsoletePositions,
                obsoleteDepartments,
                obsoleteBusinessUnits,
                previewOnly ? remainingPositions - obsoletePositions : remainingPositions,
                previewOnly ? remainingDepartments - obsoleteDepartments : remainingDepartments,
                previewOnly ? remainingBusinessUnits - obsoleteBusinessUnits : remainingBusinessUnits);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task CreateImportTable(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            CREATE TEMP TABLE employee_all_import
            (
                source_row INTEGER NOT NULL,
                employee_code TEXT NOT NULL PRIMARY KEY,
                department TEXT,
                start_date DATE,
                position_name TEXT,
                business_unit TEXT,
                title TEXT,
                first_name_th TEXT,
                last_name_th TEXT,
                first_name_en TEXT,
                last_name_en TEXT,
                supervisor_name TEXT,
                supervisor_employee_code TEXT,
                was_existing BOOLEAN NOT NULL DEFAULT FALSE
            ) ON COMMIT DROP
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task InsertImportRows(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyList<EmployeeAllRow> rows, CancellationToken token)
    {
        const string sql = """
            INSERT INTO employee_all_import
                (source_row,employee_code,department,start_date,position_name,business_unit,title,
                 first_name_th,last_name_th,first_name_en,last_name_en,supervisor_name,
                 supervisor_employee_code,was_existing)
            VALUES
                (@row,@code,@department,@start,@position,@bu,@title,
                 @first_th,@last_th,@first_en,@last_en,@boss,@boss_code,
                 EXISTS(
                     SELECT 1 FROM public.employees
                     WHERE CASE
                         WHEN employee_code ~ '^[0-9]+$' AND LENGTH(employee_code)<6
                             THEN LPAD(employee_code,6,'0')
                         ELSE employee_code
                     END=@code))
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add("row", NpgsqlDbType.Integer);
        command.Parameters.Add("code", NpgsqlDbType.Text);
        command.Parameters.Add("department", NpgsqlDbType.Text);
        command.Parameters.Add("start", NpgsqlDbType.Date);
        command.Parameters.Add("position", NpgsqlDbType.Text);
        command.Parameters.Add("bu", NpgsqlDbType.Text);
        command.Parameters.Add("title", NpgsqlDbType.Text);
        command.Parameters.Add("first_th", NpgsqlDbType.Text);
        command.Parameters.Add("last_th", NpgsqlDbType.Text);
        command.Parameters.Add("first_en", NpgsqlDbType.Text);
        command.Parameters.Add("last_en", NpgsqlDbType.Text);
        command.Parameters.Add("boss", NpgsqlDbType.Text);
        command.Parameters.Add("boss_code", NpgsqlDbType.Text);
        await command.PrepareAsync(token);

        foreach (var row in rows)
        {
            command.Parameters["row"].Value = row.SourceRow;
            command.Parameters["code"].Value = row.EmployeeCode;
            Set(command, "department", row.Department);
            command.Parameters["start"].Value = row.StartDate is null ? DBNull.Value : row.StartDate.Value;
            Set(command, "position", row.Position);
            Set(command, "bu", row.BusinessUnit);
            Set(command, "title", row.Title);
            Set(command, "first_th", row.FirstNameTh);
            Set(command, "last_th", row.LastNameTh);
            Set(command, "first_en", row.FirstNameEn);
            Set(command, "last_en", row.LastNameEn);
            Set(command, "boss", row.SupervisorName);
            Set(command, "boss_code", row.SupervisorEmployeeCode);
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task UpsertMasters(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        var commands = new[]
        {
            """
            INSERT INTO public.system_master_items(category_code,item_code,name_th,name_en,is_active)
            SELECT DISTINCT 'BUSINESS_UNIT', business_unit, business_unit, business_unit, TRUE
            FROM employee_all_import WHERE COALESCE(BTRIM(business_unit),'')<>''
            ON CONFLICT DO NOTHING;
            UPDATE public.system_master_items master SET name_th=source.business_unit,
                name_en=source.business_unit,is_active=TRUE
            FROM (SELECT DISTINCT business_unit FROM employee_all_import
                  WHERE COALESCE(BTRIM(business_unit),'')<>'') source
            WHERE master.category_code='BUSINESS_UNIT' AND master.parent_item_id IS NULL
              AND master.item_code=source.business_unit;
            """,
            """
            INSERT INTO public.system_master_items
                (category_code,parent_item_id,item_code,name_th,name_en,is_active)
            SELECT DISTINCT 'DEPARTMENT',bu.id,source.department,source.department,source.department,TRUE
            FROM employee_all_import source
            JOIN public.system_master_items bu ON bu.category_code='BUSINESS_UNIT'
             AND bu.parent_item_id IS NULL AND bu.item_code=source.business_unit
            WHERE COALESCE(BTRIM(source.department),'')<>''
            ON CONFLICT DO NOTHING;
            UPDATE public.system_master_items master SET name_th=source.department,
                name_en=source.department,is_active=TRUE
            FROM (SELECT DISTINCT bu.id bu_id,import.department
                  FROM employee_all_import import
                  JOIN public.system_master_items bu ON bu.category_code='BUSINESS_UNIT'
                   AND bu.parent_item_id IS NULL AND bu.item_code=import.business_unit
                  WHERE COALESCE(BTRIM(import.department),'')<>'') source
            WHERE master.category_code='DEPARTMENT' AND master.parent_item_id=source.bu_id
              AND master.item_code=source.department;
            """,
            """
            INSERT INTO public.system_master_items
                (category_code,parent_item_id,item_code,name_th,name_en,is_active)
            SELECT DISTINCT 'POSITION',department.id,source.position_name,
                   source.position_name,source.position_name,TRUE
            FROM employee_all_import source
            JOIN public.system_master_items bu ON bu.category_code='BUSINESS_UNIT'
             AND bu.parent_item_id IS NULL AND bu.item_code=source.business_unit
            JOIN public.system_master_items department ON department.category_code='DEPARTMENT'
             AND department.parent_item_id=bu.id AND department.item_code=source.department
            WHERE COALESCE(BTRIM(source.position_name),'')<>''
            ON CONFLICT DO NOTHING;
            UPDATE public.system_master_items master SET name_th=source.position_name,
                name_en=source.position_name,is_active=TRUE
            FROM (SELECT DISTINCT department.id department_id,import.position_name
                  FROM employee_all_import import
                  JOIN public.system_master_items bu ON bu.category_code='BUSINESS_UNIT'
                   AND bu.parent_item_id IS NULL AND bu.item_code=import.business_unit
                  JOIN public.system_master_items department ON department.category_code='DEPARTMENT'
                   AND department.parent_item_id=bu.id AND department.item_code=import.department
                  WHERE COALESCE(BTRIM(import.position_name),'')<>'') source
            WHERE master.category_code='POSITION' AND master.parent_item_id=source.department_id
              AND master.item_code=source.position_name;
            """
        };

        foreach (var sql in commands)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task UpsertEmployees(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE public.employees employee
            SET is_active=TRUE,source_row=source.source_row
            FROM employee_all_import source
            WHERE CASE
                WHEN employee.employee_code ~ '^[0-9]+$' AND LENGTH(employee.employee_code)<6
                    THEN LPAD(employee.employee_code,6,'0')
                ELSE employee.employee_code
            END=source.employee_code;

            INSERT INTO public.employees(employee_code,is_active,source_system,source_row)
            SELECT source.employee_code,TRUE,'EmployeeAll.xlsx',source.source_row
            FROM employee_all_import source
            WHERE NOT EXISTS
            (
                SELECT 1 FROM public.employees employee
                WHERE CASE
                    WHEN employee.employee_code ~ '^[0-9]+$' AND LENGTH(employee.employee_code)<6
                        THEN LPAD(employee.employee_code,6,'0')
                    ELSE employee.employee_code
                END=source.employee_code
            )
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task ResolveDatabaseSupervisorCodes(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE employee_all_import source
            SET supervisor_employee_code=employee.employee_code
            FROM public.employees employee
            WHERE CASE
                WHEN employee.employee_code ~ '^[0-9]+$' AND LENGTH(employee.employee_code)<6
                    THEN LPAD(employee.employee_code,6,'0')
                ELSE employee.employee_code
            END=source.supervisor_employee_code
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task UpsertEmployeeDetails(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        var commands = new[]
        {
            """
            INSERT INTO public.employee_basic_info
                (employee_id,title,first_name_th,last_name_th,full_name_th,
                 first_name_en,last_name_en,full_name_en)
            SELECT employee.id,source.title,source.first_name_th,source.last_name_th,
                   CONCAT_WS(' ',source.first_name_th,source.last_name_th),
                   source.first_name_en,source.last_name_en,
                   CONCAT_WS(' ',source.first_name_en,source.last_name_en)
            FROM employee_all_import source
            JOIN public.employees employee ON CASE
                WHEN employee.employee_code ~ '^[0-9]+$' AND LENGTH(employee.employee_code)<6
                    THEN LPAD(employee.employee_code,6,'0')
                ELSE employee.employee_code
            END=source.employee_code
            ON CONFLICT(employee_id) DO UPDATE SET
                title=EXCLUDED.title,first_name_th=EXCLUDED.first_name_th,
                last_name_th=EXCLUDED.last_name_th,full_name_th=EXCLUDED.full_name_th,
                first_name_en=EXCLUDED.first_name_en,last_name_en=EXCLUDED.last_name_en,
                full_name_en=EXCLUDED.full_name_en
            """,
            """
            INSERT INTO public.employee_company_info
                (employee_id,business_unit,department,position_name,start_date,
                 supervisor_name,supervisor_employee_id,leave_approver_name,
                 leave_approver_employee_id,employee_status)
            SELECT employee.id,source.business_unit,source.department,source.position_name,
                   source.start_date,source.supervisor_name,source.supervisor_employee_code,
                   source.supervisor_name,source.supervisor_employee_code,'พนักงาน'
            FROM employee_all_import source
            JOIN public.employees employee ON CASE
                WHEN employee.employee_code ~ '^[0-9]+$' AND LENGTH(employee.employee_code)<6
                    THEN LPAD(employee.employee_code,6,'0')
                ELSE employee.employee_code
            END=source.employee_code
            ON CONFLICT(employee_id) DO UPDATE SET
                business_unit=EXCLUDED.business_unit,department=EXCLUDED.department,
                position_name=EXCLUDED.position_name,start_date=EXCLUDED.start_date,
                supervisor_name=EXCLUDED.supervisor_name,
                supervisor_employee_id=EXCLUDED.supervisor_employee_id,
                leave_approver_name=EXCLUDED.leave_approver_name,
                leave_approver_employee_id=EXCLUDED.leave_approver_employee_id,
                employee_status=EXCLUDED.employee_status
            """,
            """
            INSERT INTO public.employee_personal_info(employee_id)
            SELECT employee.id FROM employee_all_import source
            JOIN public.employees employee ON CASE
                WHEN employee.employee_code ~ '^[0-9]+$' AND LENGTH(employee.employee_code)<6
                    THEN LPAD(employee.employee_code,6,'0')
                ELSE employee.employee_code
            END=source.employee_code
            ON CONFLICT(employee_id) DO NOTHING;
            INSERT INTO public.employee_family_info(employee_id)
            SELECT employee.id FROM employee_all_import source
            JOIN public.employees employee ON CASE
                WHEN employee.employee_code ~ '^[0-9]+$' AND LENGTH(employee.employee_code)<6
                    THEN LPAD(employee.employee_code,6,'0')
                ELSE employee.employee_code
            END=source.employee_code
            ON CONFLICT(employee_id) DO NOTHING;
            INSERT INTO public.employee_accounting_info(employee_id)
            SELECT employee.id FROM employee_all_import source
            JOIN public.employees employee ON CASE
                WHEN employee.employee_code ~ '^[0-9]+$' AND LENGTH(employee.employee_code)<6
                    THEN LPAD(employee.employee_code,6,'0')
                ELSE employee.employee_code
            END=source.employee_code
            ON CONFLICT(employee_id) DO NOTHING;
            """
        };

        foreach (var sql in commands)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private static void ResolveSupervisorCodes(IReadOnlyList<EmployeeAllRow> rows)
    {
        var names = rows
            .GroupBy(row => row.EnglishFullName, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().EmployeeCode,
                StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            row.SupervisorEmployeeCode = names.GetValueOrDefault(row.SupervisorName, string.Empty);
    }

    private static List<EmployeeAllRow> ReadRows(string path)
    {
        using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        var sharedStrings = ReadSharedStrings(archive);
        var sheet = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new InvalidOperationException("ไม่พบ worksheet แรก");
        using var stream = sheet.Open();
        var document = XDocument.Load(stream);
        var rows = document.Descendants(SpreadsheetNs + "row").ToList();
        if (rows.Count == 0) return [];

        var headers = ReadCells(rows[0], sharedStrings)
            .ToDictionary(pair => pair.Value.Trim(), pair => pair.Key, StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Code"] = ["รหัสพนักงาน"],
            ["แผนก"] = [],
            ["วันที่เริ่มทำงาน"] = ["วันที่เริ่มงาน"],
            ["ตำแหน่งปัจจุบัน"] = ["ตำแหน่ง"],
            ["BU"] = ["BU พนักงาน"],
            ["คำนำหน้า"] = [],
            ["ชื่อ ไทย"] = ["ชื่อ (ภาษาไทย)"],
            ["นามสกุล ไทย"] = ["นามสกุล (ภาษาไทย)"],
            ["ชื่อ EN"] = ["ชื่อ (ภาษาอังกฤษ)"],
            ["นามสกุล EN"] = ["นามสกุล (ภาษาอังกฤษ)"],
            ["Boss"] = ["ชื่อหัวหน้า"]
        };
        var missing = aliases.Where(field => !headers.ContainsKey(field.Key) &&
                !field.Value.Any(headers.ContainsKey))
            .Select(field => field.Key).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"ไม่พบคอลัมน์: {string.Join(", ", missing)}");

        var result = new List<EmployeeAllRow>();
        foreach (var rowElement in rows.Skip(1))
        {
            var values = ReadCells(rowElement, sharedStrings);
            string V(string header)
            {
                var column = headers.GetValueOrDefault(header) ??
                    aliases[header].Select(alias => headers.GetValueOrDefault(alias))
                        .FirstOrDefault(value => value is not null);
                return column is null ? string.Empty : values.GetValueOrDefault(column, string.Empty).Trim();
            }
            var code = EmployeeCodeFormat.NormalizeNew(V("Code"));
            if (string.IsNullOrWhiteSpace(code)) continue;
            result.Add(new EmployeeAllRow(
                (int?)rowElement.Attribute("r") ?? 0, code, V("แผนก"),
                ParseExcelDate(V("วันที่เริ่มทำงาน")), V("ตำแหน่งปัจจุบัน"), V("BU"),
                V("คำนำหน้า"), V("ชื่อ ไทย"), V("นามสกุล ไทย"),
                V("ชื่อ EN"), V("นามสกุล EN"), V("Boss")));
        }
        return result;
    }

    private static Dictionary<string, string> ReadCells(XElement row, IReadOnlyList<string> sharedStrings) =>
        row.Elements(SpreadsheetNs + "c").ToDictionary(
            cell => new string(((string?)cell.Attribute("r") ?? string.Empty).TakeWhile(char.IsLetter).ToArray()),
            cell => CellValue(cell, sharedStrings), StringComparer.OrdinalIgnoreCase);

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
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
        if (type == "s" && int.TryParse(raw, out var index))
            return index >= 0 && index < sharedStrings.Count ? sharedStrings[index].Trim() : string.Empty;
        return raw.Trim();
    }

    private static DateOnly? ParseExcelDate(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        return DateOnly.TryParse(value, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var date)
            ? date : null;
    }

    private static void Set(NpgsqlCommand command, string name, string value) =>
        command.Parameters[name].Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static async Task<int> ScalarInt(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteNonQuery(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(token);
    }

    private sealed record EmployeeAllRow(
        int SourceRow,
        string EmployeeCode,
        string Department,
        DateOnly? StartDate,
        string Position,
        string BusinessUnit,
        string Title,
        string FirstNameTh,
        string LastNameTh,
        string FirstNameEn,
        string LastNameEn,
        string SupervisorName)
    {
        public string SupervisorEmployeeCode { get; set; } = string.Empty;
        public string EnglishFullName => $"{FirstNameEn} {LastNameEn}".Trim();
    }
}
