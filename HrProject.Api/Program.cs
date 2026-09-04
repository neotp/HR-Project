using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using HrProject.Api.Tools;
using HrProject.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Windows Event Log requires elevated permissions on some development machines.
// Keep application logging on Console/Debug so an error response cannot crash
// the Kestrel connection while the logger attempts to write to Event Viewer.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Add services to the container.

builder.Services.AddControllers();
var connectionString = builder.Configuration.GetConnectionString("HrDatabase")
    ?? throw new InvalidOperationException("Connection string 'HrDatabase' is not configured.");

if (args.Length >= 2 && string.Equals(args[0], "--import-employees", StringComparison.OrdinalIgnoreCase))
{
    var workbookPath = Path.GetFullPath(args[1]);
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "012_create_employee_tables.sql"));
    var result = await EmployeeWorkbookImporter.ImportAsync(
        connectionString, workbookPath, migrationPath);
    Console.WriteLine($"Employee import completed: inserted={result.Inserted}, skipped={result.Skipped}.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--verify-employees", StringComparison.OrdinalIgnoreCase))
{
    var result = await EmployeeWorkbookImporter.VerifyAsync(connectionString);
    Console.WriteLine(
        $"employees={result.Employees}, basic={result.BasicInfo}, company={result.CompanyInfo}, " +
        $"personal={result.PersonalInfo}, family={result.FamilyInfo}");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-type-default-hours", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "013_add_leave_type_default_hours.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added leave_types.default_hours successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-bonus-deduction", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "034_add_leave_bonus_deduction.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added leave bonus deduction policy and document snapshots successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-quota-movements", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "035_create_leave_quota_movements.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created leave quota movement ledger successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-attendance-pipeline", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "036_create_attendance_pipeline.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created attendance import and calculation pipeline successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-attendance-responses", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "037_create_attendance_responses.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created attendance response and attachment tables successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-attendance-reviews", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "038_create_attendance_review_page.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created attendance review page, actions and calculated result columns successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-company-calendar-outlook", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "039_create_company_calendar_outlook_sync.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created company calendar Outlook synchronization queue successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--verify-attendance-pipeline", StringComparison.OrdinalIgnoreCase))
{
    await using var verifyDataSource = NpgsqlDataSource.Create(connectionString);
    await using var verifyCommand = verifyDataSource.CreateCommand("""
        SELECT
            (SELECT COUNT(*) FROM public.attendance_raw_scans),
            (SELECT COUNT(*) FROM public.attendance_daily_records),
            (SELECT COUNT(*) FROM public.attendance_daily_records WHERE requires_review),
            (SELECT last_captured_at FROM public.attendance_sync_states WHERE source_system = 'HIKVISION'),
            (SELECT last_error FROM public.attendance_sync_states WHERE source_system = 'HIKVISION')
        """);
    await using var reader = await verifyCommand.ExecuteReaderAsync();
    await reader.ReadAsync();
    Console.WriteLine(
        $"rawScans={reader.GetInt64(0)}, dailyRecords={reader.GetInt64(1)}, " +
        $"requiresReview={reader.GetInt64(2)}, " +
        $"lastCapturedAt={(reader.IsDBNull(3) ? "-" : reader.GetDateTime(3).ToString("yyyy-MM-dd HH:mm:ss"))}, " +
        $"lastError={(reader.IsDBNull(4) ? "-" : reader.GetString(4))}");
    await reader.DisposeAsync();

    if (args.Length >= 2)
    {
        await using var employeeCommand = verifyDataSource.CreateCommand("""
            SELECT
                (SELECT COUNT(*) FROM public.attendance_raw_scans
                  WHERE source_employee_id = @employee_id AND captured_at::date = CURRENT_DATE),
                (SELECT MIN(captured_at) FROM public.attendance_raw_scans
                  WHERE source_employee_id = @employee_id AND captured_at::date = CURRENT_DATE),
                (SELECT MAX(captured_at) FROM public.attendance_raw_scans
                  WHERE source_employee_id = @employee_id AND captured_at::date = CURRENT_DATE),
                (SELECT final_status FROM public.attendance_daily_records
                  WHERE employee_id = @employee_id AND work_date = CURRENT_DATE),
                (SELECT scan_count FROM public.attendance_daily_records
                  WHERE employee_id = @employee_id AND work_date = CURRENT_DATE)
            """);
        employeeCommand.Parameters.AddWithValue("employee_id", args[1]);
        await using var employeeReader = await employeeCommand.ExecuteReaderAsync();
        await employeeReader.ReadAsync();
        Console.WriteLine(
            $"employee={args[1]}, todayRawScans={employeeReader.GetInt64(0)}, " +
            $"firstScan={(employeeReader.IsDBNull(1) ? "-" : employeeReader.GetDateTime(1).ToString("HH:mm:ss"))}, " +
            $"lastScan={(employeeReader.IsDBNull(2) ? "-" : employeeReader.GetDateTime(2).ToString("HH:mm:ss"))}, " +
            $"dailyStatus={(employeeReader.IsDBNull(3) ? "-" : employeeReader.GetString(3))}, " +
            $"dailyScanCount={(employeeReader.IsDBNull(4) ? "-" : employeeReader.GetInt32(4))}");
    }
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--cleanup-future-attendance", StringComparison.OrdinalIgnoreCase))
{
    await using var cleanupDataSource = NpgsqlDataSource.Create(connectionString);
    await using var cleanupCommand = cleanupDataSource.CreateCommand("""
        DELETE FROM public.attendance_daily_records
        WHERE work_date > (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Bangkok')::date
        """);
    var deleted = await cleanupCommand.ExecuteNonQueryAsync();
    Console.WriteLine($"Deleted future attendance records: {deleted}");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--seed-leave-quotas", StringComparison.OrdinalIgnoreCase))
{
    var quotaYear = args.Length >= 2 && int.TryParse(args[1], out var requestedYear)
        ? requestedYear
        : DateTime.Today.Year;
    if (quotaYear is < 2000 or > 2200)
        throw new ArgumentOutOfRangeException(nameof(quotaYear), "Quota year must be between 2000 and 2200.");

    var seedPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "014_seed_employee_leave_quotas.sql"));
    var seedSql = await File.ReadAllTextAsync(seedPath);
    await using var quotaDataSource = NpgsqlDataSource.Create(connectionString);
    await using (var seedCommand = quotaDataSource.CreateCommand(seedSql))
    {
        seedCommand.Parameters.AddWithValue("quota_year", quotaYear);
        var created = await seedCommand.ExecuteNonQueryAsync();
        Console.WriteLine($"Created {created} leave quota rows for {quotaYear}.");
    }

    const string verifySql = """
        SELECT
            (SELECT COUNT(*) FROM public.employees WHERE is_active = TRUE),
            (SELECT COUNT(*) FROM public.leave_types WHERE is_active = TRUE),
            (SELECT COUNT(*) FROM public.leave_quotas WHERE quota_year = @quota_year)
        """;
    await using var verifyCommand = quotaDataSource.CreateCommand(verifySql);
    verifyCommand.Parameters.AddWithValue("quota_year", quotaYear);
    await using var quotaReader = await verifyCommand.ExecuteReaderAsync();
    await quotaReader.ReadAsync();
    Console.WriteLine(
        $"activeEmployees={quotaReader.GetInt64(0)}, activeLeaveTypes={quotaReader.GetInt64(1)}, " +
        $"quotasForYear={quotaReader.GetInt64(2)}");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-system-master-data", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "015_create_system_master_data.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created system master data successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--link-departments-to-business-units", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "016_link_departments_to_business_units.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Linked departments to business units successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-page-action-permissions", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "017_create_page_action_permissions.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created page action permissions successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--disable-direct-employee-edit", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "050_disable_direct_employee_edit.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Disabled direct employee editing successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-app-roles", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "018_create_app_roles.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created application roles successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-all-leave-documents-page", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "019_add_all_leave_documents_page.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created all leave documents page permission successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-medical-certificate", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "020_add_leave_medical_certificate.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added sick-leave medical certificate field successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-edit-request-medical-certificate", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "027_add_edit_request_medical_certificate.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added medical certificate field to leave edit requests successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-cancel-requests", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "028_create_leave_cancel_requests.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created leave cancellation request workflow successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-cancel-history-actions", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "030_add_leave_cancel_history_actions.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added leave cancellation history actions successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-outlook-calendar", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "031_create_leave_outlook_calendar_sync.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created leave Outlook calendar synchronization table successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-page-availability", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "032_add_application_page_availability.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added global application page availability switches successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-manager-notification-leave-type", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "033_add_manager_notification_leave_type.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added leave type to manager leave notifications successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--check-leave-outlook-calendar", StringComparison.OrdinalIgnoreCase))
{
    const string diagnosticSql = """
        SELECT d.document_no, d.status, c.sync_status, COALESCE(c.last_action, '-'),
               c.retry_count, COALESCE(c.last_sync_error, '-'), c.last_attempted_at,
               c.outlook_event_id IS NOT NULL, COALESCE(c.employee_email, '-'),
               COALESCE(c.outlook_web_link, '-'), d.leave_date, d.start_time, d.leave_hours
        FROM public.leave_calendar_events c
        JOIN public.leave_documents d ON d.id = c.leave_document_id
        WHERE (@document_no IS NULL OR d.document_no = @document_no)
        ORDER BY c.updated_at DESC
        LIMIT 10
        """;
    await using var diagnosticDataSource = NpgsqlDataSource.Create(connectionString);
    await using var diagnosticCommand = diagnosticDataSource.CreateCommand(diagnosticSql);
    diagnosticCommand.Parameters.Add(new NpgsqlParameter<string?>(
        "document_no", args.Length >= 2 ? args[1] : null));
    await using var diagnosticReader = await diagnosticCommand.ExecuteReaderAsync();
    while (await diagnosticReader.ReadAsync())
    {
        Console.WriteLine(
            $"document={diagnosticReader.GetString(0)}, documentStatus={diagnosticReader.GetString(1)}, " +
            $"syncStatus={diagnosticReader.GetString(2)}, action={diagnosticReader.GetString(3)}, " +
            $"retries={diagnosticReader.GetInt32(4)}, hasEventId={diagnosticReader.GetBoolean(7)}, " +
            $"mailbox={diagnosticReader.GetString(8)}, " +
            $"attemptedAt={(diagnosticReader.IsDBNull(6) ? "-" : diagnosticReader.GetFieldValue<DateTimeOffset>(6).ToString("O"))}");
        Console.WriteLine($"error={diagnosticReader.GetString(5)}");
        Console.WriteLine($"webLink={diagnosticReader.GetString(9)}");
        Console.WriteLine(
            $"leaveDate={diagnosticReader.GetFieldValue<DateOnly>(10).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}, " +
            $"startTime={diagnosticReader.GetFieldValue<TimeOnly>(11):HH\\:mm}, " +
            $"hours={diagnosticReader.GetDecimal(12):0.##}");
    }
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-attendance-records-page", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "029_add_attendance_records_page.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added attendance records page successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-manager-leave-notifications", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "021_create_manager_leave_notifications.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created manager leave notification history successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-team-action-permission", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "022_update_leave_team_action_permission.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Updated leave team additional action permission successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--split-maternity-ordination-leave-types", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "023_split_maternity_and_ordination_leave_types.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    const string verifySql = """
        SELECT t.code, t.name_th, t.default_hours, COUNT(q.id)
        FROM public.leave_types t
        LEFT JOIN public.leave_quotas q ON q.leave_type_id = t.id
        WHERE t.code IN ('UNPAID', 'ORDINATION')
        GROUP BY t.id, t.code, t.name_th, t.default_hours
        ORDER BY t.code
        """;
    await using var verifyCommand = migrationDataSource.CreateCommand(verifySql);
    await using var verifyReader = await verifyCommand.ExecuteReaderAsync();
    while (await verifyReader.ReadAsync())
    {
        Console.WriteLine(
            $"code={verifyReader.GetString(0)}, name={verifyReader.GetString(1)}, " +
            $"defaultHours={verifyReader.GetDecimal(2):0.##}, quotas={verifyReader.GetInt64(3)}");
    }
    Console.WriteLine("Separated maternity and ordination leave types successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-manager-notification-leave-hours", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "024_add_manager_notification_leave_hours.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added leave hours to manager leave notifications successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-attachments-to-database", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "025_store_leave_attachments_in_database.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Enabled PostgreSQL BYTEA storage for leave attachments successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-edit-request-attachments", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "026_add_edit_request_attachments.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Enabled additive attachments for leave edit requests successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-attendance-exclusion", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "040_add_employee_attendance_calculation_exclusion.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added employee attendance calculation exclusion flag successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-attendance-recalculation-queue", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "041_create_attendance_recalculation_queue.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created attendance recalculation queue successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-attendance-calendar-events", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "042_create_attendance_calendar_events.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created personal attendance calendar events successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-work-calendar-attendance-queue", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "043_queue_attendance_from_work_calendar.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Connected company work calendar changes to attendance recalculation successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-work-calendar-document-templates", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "044_create_work_calendar_document_templates.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created versioned work calendar document templates successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-attendance-event-types", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "045_create_attendance_event_type_master.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created attendance event type master data successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-attendance-event-reviews", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "046_add_attendance_calendar_event_review.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added review workflow and creation audit to attendance calendar events successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-pre-employee-page", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "047_add_pre_employee_page.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added Pre-Employee application page successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-pre-employees", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "048_create_pre_employees.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created Pre-Employee staging workflow successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-pre-employee-full-data", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "049_add_pre_employee_full_data.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added full Employee draft data to Pre-Employee successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-resigned-employees-page", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "052_add_resigned_employees_page.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added resigned employees application page successfully.");
    return;
}

// The database is hosted on another server. Keep pooled connections alive so a
// firewall/NAT idle timeout does not hand a dead connector to background jobs.
var pooledConnectionSettings = new NpgsqlConnectionStringBuilder(connectionString);
if (pooledConnectionSettings.KeepAlive == 0)
    pooledConnectionSettings.KeepAlive = 30;
builder.Services.AddSingleton(NpgsqlDataSource.Create(pooledConnectionSettings.ConnectionString));
builder.Services.AddSingleton<PageActionPermissionService>();
builder.Services.AddSingleton<PageAccessService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<MicrosoftGraphMailService>();
builder.Services.AddSingleton<LeaveApprovalEmailService>();
builder.Services.AddSingleton<LeaveDecisionEmailService>();
builder.Services.AddSingleton<LeaveCancellationEmailService>();
builder.Services.AddSingleton<OutlookCalendarSyncService>();
builder.Services.AddHostedService<OutlookCalendarRetryWorker>();
builder.Services.AddSingleton<CompanyCalendarOutlookSyncService>();
builder.Services.AddHostedService<CompanyCalendarOutlookRetryWorker>();
var tenantId = builder.Configuration["AzureAd:TenantId"]
    ?? throw new InvalidOperationException("AzureAd:TenantId is not configured.");
var clientId = builder.Configuration["AzureAd:ClientId"]
    ?? throw new InvalidOperationException("AzureAd:ClientId is not configured.");
var requiredScope = builder.Configuration["AzureAd:Scope"] ?? "users.read";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidAudiences =
            [
                $"api://{clientId}",
                clientId
            ],
            ValidIssuers =
            [
                $"https://login.microsoftonline.com/{tenantId}/v2.0",
                $"https://sts.windows.net/{tenantId}/"
            ],
            NameClaimType = "name"
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("HrApiScope", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            var scopes = context.User.FindFirst("scp")?.Value?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            return scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase) ||
                   context.User.HasClaim("roles", "Users.Read");
        });
    });
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context =>
        {
            var scopes = context.User.FindFirst("scp")?.Value?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            return scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase) ||
                   context.User.HasClaim("roles", "Users.Read");
        })
        .Build();
});
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("https://localhost:7169", "http://localhost:5043")
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

if (args.Length >= 1 && string.Equals(args[0], "--check-company-calendar-outlook", StringComparison.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var syncDataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
    await using var command = syncDataSource.CreateCommand("""
        SELECT COUNT(*),
               COUNT(*) FILTER (WHERE sync_status = 'PENDING'),
               COUNT(*) FILTER (WHERE sync_status = 'SYNCED'),
               COUNT(*) FILTER (WHERE sync_status = 'FAILED'),
               COUNT(*) FILTER (WHERE sync_status = 'DELETED'),
               COUNT(DISTINCT employee_id), COUNT(DISTINCT calendar_date)
        FROM public.work_calendar_outlook_events
        """);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    Console.WriteLine(
        $"total={reader.GetInt64(0)}, pending={reader.GetInt64(1)}, synced={reader.GetInt64(2)}, " +
        $"failed={reader.GetInt64(3)}, deleted={reader.GetInt64(4)}, " +
        $"employees={reader.GetInt64(5)}, dates={reader.GetInt64(6)}");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--sync-company-calendar-outlook-batch", StringComparison.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var calendarSync = scope.ServiceProvider.GetRequiredService<CompanyCalendarOutlookSyncService>();
    var limit = args.Length >= 2 && int.TryParse(args[1], out var requestedLimit)
        ? Math.Clamp(requestedLimit, 1, 500)
        : 10;
    var ids = await calendarSync.LoadPendingIds(limit, CancellationToken.None);
    var succeeded = 0;
    var failed = 0;
    await Parallel.ForEachAsync(ids,
        new ParallelOptions { MaxDegreeOfParallelism = 4 },
        async (id, token) =>
        {
            try
            {
                await calendarSync.SyncAsync(id, token);
                Interlocked.Increment(ref succeeded);
            }
            catch
            {
                Interlocked.Increment(ref failed);
            }
        });
    Console.WriteLine($"Company calendar Outlook batch completed: succeeded={succeeded}, failed={failed}.");
    return;
}

if (args.Length >= 2 && string.Equals(args[0], "--sync-leave-outlook-calendar", StringComparison.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var syncDataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
    await using var findCommand = syncDataSource.CreateCommand(
        "SELECT id FROM public.leave_documents WHERE document_no = @document_no");
    findCommand.Parameters.AddWithValue("document_no", args[1]);
    var documentId = await findCommand.ExecuteScalarAsync();
    if (documentId is null)
        throw new InvalidOperationException($"Leave document '{args[1]}' was not found.");

    var calendarSync = scope.ServiceProvider.GetRequiredService<OutlookCalendarSyncService>();
    await calendarSync.SyncAsync(Convert.ToInt64(documentId), CancellationToken.None);
    Console.WriteLine($"Synchronized Outlook Calendar for {args[1]} successfully.");
    return;
}

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/api/health/database", async (NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
{
    await using var command = dataSource.CreateCommand("SELECT current_database(), current_schema(), now()");
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    await reader.ReadAsync(cancellationToken);
    return Results.Ok(new
    {
        Database = reader.GetString(0),
        Schema = reader.GetString(1),
        ServerTime = reader.GetDateTime(2)
    });
}).AllowAnonymous();

app.Run();
