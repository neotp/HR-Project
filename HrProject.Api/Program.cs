using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using HrProject.Api.Tools;
using HrProject.Api.Services;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Windows Event Log requires elevated permissions on some development machines.
// Keep application logging on Console/Debug so an error response cannot crash
// the Kestrel connection while the logger attempts to write to Event Viewer.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
});
var connectionString = builder.Configuration.GetConnectionString("HrDatabase")
    ?? throw new InvalidOperationException("Connection string 'HrDatabase' is not configured.");

if (args.Length >= 3 &&
    (string.Equals(args[0], "--preview-product-target-import", StringComparison.OrdinalIgnoreCase) ||
     string.Equals(args[0], "--import-product-targets", StringComparison.OrdinalIgnoreCase) ||
     string.Equals(args[0], "--verify-product-targets", StringComparison.OrdinalIgnoreCase)))
{
    var preview = string.Equals(args[0], "--preview-product-target-import", StringComparison.OrdinalIgnoreCase);
    var verifyOnly = string.Equals(args[0], "--verify-product-targets", StringComparison.OrdinalIgnoreCase);
    var result = await EmployeeProductTargetWorkbookImporter.RunAsync(
        connectionString, Path.GetFullPath(args[1]), args[2], preview, verifyOnly);
    Console.WriteLine($"Product target {(preview ? "preview" : verifyOnly ? "verification" : "import")}: rows={result.SourceRows}, " +
        $"employees={result.Employees}, matchedEmployees={result.MatchedEmployees}, " +
        $"missingCodes={string.Join(",", result.MissingEmployeeCodes)}, productBU={result.ProductBusinessUnits}, " +
        $"brands={result.Brands}, commGroups={result.CommGroups}, currentAssignments={result.CurrentAssignments}, " +
        $"extraCurrentAssignments={result.ExtraCurrentAssignments}");
    return;
}

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

if (args.Length >= 2 && string.Equals(args[0], "--import-employee-all", StringComparison.OrdinalIgnoreCase))
{
    var workbookPath = Path.GetFullPath(args[1]);
    var quotaTriggerMigrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "096_fix_initial_vacation_quota_year.sql"));
    var quotaTriggerMigrationSql = await File.ReadAllTextAsync(quotaTriggerMigrationPath);
    await using (var migrationDataSource = NpgsqlDataSource.Create(connectionString))
    await using (var migrationCommand = migrationDataSource.CreateCommand(quotaTriggerMigrationSql))
        await migrationCommand.ExecuteNonQueryAsync();
    var result = await EmployeeAllWorkbookImporter.ImportAsync(connectionString, workbookPath);
    Console.WriteLine(
        $"EmployeeAll import completed: rows={result.SourceRows}, inserted={result.InsertedEmployees}, " +
        $"updated={result.UpdatedEmployees}, bu={result.BusinessUnits}, departments={result.Departments}, " +
        $"positions={result.Positions}, supervisors={result.LinkedSupervisors}, " +
        $"missingOrganization={result.MissingOrganizationRows}.");
    return;
}

if (args.Length >= 2 && string.Equals(args[0], "--repair-import-employee-all", StringComparison.OrdinalIgnoreCase))
{
    var workbookPath = Path.GetFullPath(args[1]);
    var quotaTriggerMigrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "096_fix_initial_vacation_quota_year.sql"));
    var quotaTriggerMigrationSql = await File.ReadAllTextAsync(quotaTriggerMigrationPath);
    await using (var migrationDataSource = NpgsqlDataSource.Create(connectionString))
    await using (var migrationCommand = migrationDataSource.CreateCommand(quotaTriggerMigrationSql))
        await migrationCommand.ExecuteNonQueryAsync();
    var result = await EmployeeAllWorkbookImporter.ImportAsync(
        connectionString, workbookPath, removePreviousImport: true);
    Console.WriteLine(
        $"EmployeeAll repair completed: rows={result.SourceRows}, inserted={result.InsertedEmployees}, " +
        $"updated={result.UpdatedEmployees}, bu={result.BusinessUnits}, departments={result.Departments}, " +
        $"positions={result.Positions}, supervisors={result.LinkedSupervisors}, " +
        $"missingOrganization={result.MissingOrganizationRows}.");
    return;
}

if (args.Length >= 2 && string.Equals(args[0], "--verify-employee-all", StringComparison.OrdinalIgnoreCase))
{
    var workbookPath = Path.GetFullPath(args[1]);
    var result = await EmployeeAllWorkbookImporter.VerifyAsync(connectionString, workbookPath);
    Console.WriteLine(
        $"EmployeeAll verification: rows={result.SourceRows}, matched={result.MatchedEmployees}, " +
        $"duplicateCanonicalCodes={result.DuplicateCanonicalCodes}, dataMismatches={result.EmployeeDataMismatches}, " +
        $"missingMasterPaths={result.MissingMasterPaths}, invalidSupervisorLinks={result.InvalidSupervisorLinks}, " +
        $"totalEmployees={result.TotalEmployees}.");
    return;
}

if (args.Length >= 2 &&
    (string.Equals(args[0], "--preview-cleanup-employee-all-masters", StringComparison.OrdinalIgnoreCase) ||
     string.Equals(args[0], "--cleanup-employee-all-masters", StringComparison.OrdinalIgnoreCase)))
{
    var workbookPath = Path.GetFullPath(args[1]);
    var previewOnly = string.Equals(
        args[0], "--preview-cleanup-employee-all-masters", StringComparison.OrdinalIgnoreCase);
    var result = await EmployeeAllWorkbookImporter.CleanupMastersAsync(
        connectionString, workbookPath, previewOnly);
    Console.WriteLine(
        $"EmployeeAll master cleanup{(result.IsPreview ? " preview" : string.Empty)}: " +
        $"positionsRemoved={result.DeletedPositions}, departmentsRemoved={result.DeletedDepartments}, " +
        $"businessUnitsRemoved={result.DeletedBusinessUnits}, " +
        $"remainingPositions={result.RemainingPositions}, remainingDepartments={result.RemainingDepartments}, " +
        $"remainingBusinessUnits={result.RemainingBusinessUnits}.");
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

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-work-history-columns", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "097_expand_employee_work_history.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Employee work-history columns migration completed.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-activity-changes", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "098_add_employee_activity_changes.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Employee activity field changes migration completed.");
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

if (args.Length >= 1 && string.Equals(args[0], "--migrate-attendance-wifi", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "099_create_attendance_wifi_connections.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created attendance Wi-Fi import tables successfully.");
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

if (args.Length >= 1 && string.Equals(args[0], "--migrate-attendance-event-outlook", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "075_create_attendance_event_outlook_sync.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created attendance event Outlook synchronization queue successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-comments", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "076_create_leave_document_comments.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created leave document comments and email notification queue successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-comment-participants", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "077_create_leave_comment_participants.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created persistent leave comment participants successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-comment-recipient-types", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "078_expand_leave_comment_recipient_types.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Expanded leave comment recipient types successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-comment-attachments", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "079_create_leave_comment_attachments.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created leave comment image attachments successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-recruit-document-links", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "080_create_employee_recruit_document_links.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created employee Recruit document links successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-attendance-comments", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "081_create_attendance_comments.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created attendance comments successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-quota-request-comments", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "082_create_leave_quota_request_comments.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created leave quota request comments successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--link-positions-to-departments", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "083_link_positions_to_departments_and_business_units.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Linked positions to departments and business units successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-accounting-info", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "084_create_employee_accounting_info.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created employee accounting information successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-address-sections", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "085_add_employee_address_sections.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Employee address sections migration completed.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-statutory-accounting-fields", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "086_add_employee_statutory_accounting_fields.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Employee statutory accounting fields migration completed.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-edit-request-attachments", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "087_create_employee_edit_request_attachments.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Employee edit request attachments migration completed.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-pre-employee-did-not-start-work", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "088_add_pre_employee_did_not_start_work.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Pre-Employee did-not-start-work migration completed.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-approved-edit-attachments-to-personal-documents", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "089_copy_approved_edit_attachments_to_personal_documents.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Approved employee edit attachments migration completed.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-quota-year-allocations", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "090_create_leave_quota_year_allocations.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    migrationCommand.CommandTimeout = 300;
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Leave quota yearly allocation migration completed.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-pre-employee-recruit-url", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "091_add_pre_employee_recruit_url.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Pre-Employee Recruit URL migration completed.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-product-targets", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "092_create_employee_product_targets.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Product BU master and employee product target migration completed.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-edit-request-workflow", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "093_employee_edit_request_workflow.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Employee edit request owner workflow and comments migration completed.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-workflow-comment-attachments", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "094_create_workflow_comment_attachments.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Workflow comment attachment migration completed.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-tab-permissions", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "095_employee_tab_permissions.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    await using var verifyCommand = migrationDataSource.CreateCommand("""
        SELECT COUNT(*)
        FROM public.application_page_actions action
        JOIN public.application_pages page ON page.id = action.application_page_id
        WHERE page.page_key = 'EMPLOYEES'
          AND action.is_active = TRUE
          AND action.action_key = ANY(ARRAY[
              'VIEW_ACCOUNTING', 'VIEW_WORK_HISTORY', 'VIEW_EDUCATION',
              'VIEW_TRAINING', 'VIEW_TAX_DEDUCTION', 'VIEW_RECRUIT_DOCUMENTS',
              'VIEW_CHANGE_HISTORY', 'EDIT_INTERNAL', 'EDIT_ACCOUNTING',
              'EDIT_WORK_HISTORY', 'EDIT_TRAINING'
          ]::text[])
        """);
    var permissionCount = Convert.ToInt32(await verifyCommand.ExecuteScalarAsync());
    if (permissionCount != 11)
        throw new InvalidOperationException(
            $"Employee tab permission verification failed: expected 11 actions, found {permissionCount}.");
    Console.WriteLine($"Employee tab permission migration completed and verified ({permissionCount} actions).");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--verify-leave-quota-year-allocations", StringComparison.OrdinalIgnoreCase))
{
    await using var verifyDataSource = NpgsqlDataSource.Create(connectionString);
    await using var verifyCommand = verifyDataSource.CreateCommand("""
        SELECT
            to_regclass('public.leave_document_quota_allocations') IS NOT NULL AS allocation_table_exists,
            to_regprocedure('public.calculate_vacation_entitlement_hours(character varying,integer)') IS NOT NULL AS entitlement_function_exists,
            EXISTS
            (
                SELECT 1 FROM pg_trigger
                WHERE tgname = 'trg_leave_document_quota_allocation' AND NOT tgisinternal
            ) AS allocation_trigger_exists,
            COALESCE((
                SELECT COUNT(*) FROM public.leave_documents d
                WHERE d.status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED')
                  AND COALESCE((SELECT SUM(a.allocated_hours)
                                FROM public.leave_document_quota_allocations a
                                WHERE a.leave_document_id = d.id AND a.released_at IS NULL), 0)
                      <> d.leave_hours
            ), 0) AS active_documents_with_invalid_allocation,
            COALESCE((
                SELECT COUNT(*) FROM public.leave_document_quota_allocations a
                WHERE a.released_at IS NULL AND a.allocated_hours <= 0
            ), 0) AS invalid_active_allocations,
            COALESCE((
                SELECT COUNT(*) FROM public.leave_quotas q
                WHERE q.quota_status NOT IN ('PROJECTED', 'FINALIZED')
                   OR q.new_entitlement_hours < 0 OR q.carried_forward_hours < 0
                   OR q.annual_excess_hours < 0
            ), 0) AS invalid_quota_rows,
            (SELECT default_hours FROM public.leave_types WHERE code = 'VACATION') AS vacation_master_default_hours
        """);
    await using var reader = await verifyCommand.ExecuteReaderAsync();
    await reader.ReadAsync();
    Console.WriteLine($"allocation_table={reader.GetBoolean(0)}");
    Console.WriteLine($"entitlement_function={reader.GetBoolean(1)}");
    Console.WriteLine($"allocation_trigger={reader.GetBoolean(2)}");
    Console.WriteLine($"active_documents_with_invalid_allocation={reader.GetInt64(3)}");
    Console.WriteLine($"invalid_active_allocations={reader.GetInt64(4)}");
    Console.WriteLine($"invalid_quota_rows={reader.GetInt64(5)}");
    Console.WriteLine($"vacation_master_default_hours={(reader.IsDBNull(6) ? 0 : reader.GetDecimal(6))}");
    await reader.CloseAsync();

    await using var connection = await verifyDataSource.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    try
    {
        var employeeCode = $"__QUOTA_VERIFY_{Guid.NewGuid():N}";
        long employeeId;
        await using (var insertEmployee = new NpgsqlCommand(
            "INSERT INTO public.employees(employee_code,is_active,source_system) VALUES(@code,TRUE,'VERIFY') RETURNING id",
            connection, transaction))
        {
            insertEmployee.Parameters.AddWithValue("code", employeeCode);
            employeeId = (long)(await insertEmployee.ExecuteScalarAsync())!;
        }
        await InitialLeaveQuotaService.CreateForNewEmployee(
            connection, transaction, employeeCode, new DateOnly(2026, 7, 1),
            "VERIFY", "Verification", CancellationToken.None);
        await using (var insertCompany = new NpgsqlCommand(
            "INSERT INTO public.employee_company_info(employee_id,start_date) VALUES(@id,DATE '2026-07-01')",
            connection, transaction))
        {
            insertCompany.Parameters.AddWithValue("id", employeeId);
            await insertCompany.ExecuteNonQueryAsync();
        }

        await using (var initialQuotaCheck = new NpgsqlCommand("""
            SELECT
                COUNT(*) = (SELECT COUNT(*) FROM public.leave_types WHERE is_active=TRUE),
                COALESCE(MAX(q.quota_hours) FILTER (WHERE t.code='VACATION'),-1),
                COUNT(h.id) = COUNT(q.id)
            FROM public.leave_quotas q
            JOIN public.leave_types t ON t.id=q.leave_type_id
            LEFT JOIN public.leave_quota_history h
              ON h.leave_quota_id=q.id AND h.action='CREATE'
            WHERE q.employee_id=@employee AND q.quota_year=2026
            """, connection, transaction))
        {
            initialQuotaCheck.Parameters.AddWithValue("employee", employeeCode);
            await using var initialQuotaReader = await initialQuotaCheck.ExecuteReaderAsync();
            await initialQuotaReader.ReadAsync();
            if (!initialQuotaReader.GetBoolean(0) || initialQuotaReader.GetDecimal(1) != 24 ||
                !initialQuotaReader.GetBoolean(2))
                throw new InvalidOperationException("Pre-Employee initial quota scenario failed.");
        }

        long vacationTypeId;
        await using (var typeCommand = new NpgsqlCommand(
            "SELECT id FROM public.leave_types WHERE code='VACATION'", connection, transaction))
            vacationTypeId = (long)(await typeCommand.ExecuteScalarAsync())!;

        await using (var projectedQuota = new NpgsqlCommand("""
            INSERT INTO public.leave_quotas
                (employee_id,leave_type_id,quota_year,quota_hours,used_hours,notes,
                 created_by,created_by_name,updated_by,updated_by_name,quota_status,
                 new_entitlement_hours,carried_forward_hours)
            VALUES(@employee,@type,2027,64,0,'Verification scenario','SYSTEM','VERIFY','SYSTEM','VERIFY',
                   'PROJECTED',40,24)
            ON CONFLICT(employee_id,leave_type_id,quota_year) DO UPDATE SET
                quota_hours=64,new_entitlement_hours=40,carried_forward_hours=24,quota_status='PROJECTED'
            """, connection, transaction))
        {
            projectedQuota.Parameters.AddWithValue("employee", employeeCode);
            projectedQuota.Parameters.AddWithValue("type", vacationTypeId);
            await projectedQuota.ExecuteNonQueryAsync();
        }

        async Task<long> InsertDocument(string number, decimal hours)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO public.leave_documents
                    (document_no,creator_employee_id,creator_name,creator_department,
                     approver_name,leave_type_id,leave_kind,leave_date,start_time,
                     leave_hours,leave_reason,status)
                VALUES(@number,@employee,'Verify employee','Verify','Verify approver',@type,
                       'ADVANCE',DATE '2027-07-01',TIME '09:00',@hours,'Verification','PENDING_APPROVAL')
                RETURNING id
                """, connection, transaction);
            command.Parameters.AddWithValue("number", number);
            command.Parameters.AddWithValue("employee", employeeCode);
            command.Parameters.AddWithValue("type", vacationTypeId);
            command.Parameters.AddWithValue("hours", hours);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        var firstDocument = await InsertDocument($"QV-{Guid.NewGuid():N}"[..30], 8);
        var scenarioDocuments = new List<long> { firstDocument };
        for (var index = 0; index < 5; index++)
            scenarioDocuments.Add(await InsertDocument($"QV-{Guid.NewGuid():N}"[..30], 8));
        var spillDocument = scenarioDocuments[^1];

        await using (var scenarioCommand = new NpgsqlCommand("""
            SELECT
                COALESCE(SUM(allocated_hours) FILTER (WHERE source_quota_year=2027),0),
                COALESCE(SUM(allocated_hours) FILTER (WHERE source_quota_year=2026),0),
                COALESCE(SUM(allocated_hours),0)
            FROM public.leave_document_quota_allocations
            WHERE leave_document_id = ANY(@documents) AND released_at IS NULL
            """, connection, transaction))
        {
            scenarioCommand.Parameters.AddWithValue("documents", scenarioDocuments.ToArray());
            await using var scenarioReader = await scenarioCommand.ExecuteReaderAsync();
            await scenarioReader.ReadAsync();
            if (scenarioReader.GetDecimal(0) != 40 || scenarioReader.GetDecimal(1) != 8 || scenarioReader.GetDecimal(2) != 48)
                throw new InvalidOperationException("Future quota priority scenario failed.");
        }

        await using (var rolloverCheck = new NpgsqlCommand("""
            WITH source_usage AS
            (
                SELECT COALESCE(SUM(allocated_hours),0) AS used_hours,
                       COALESCE(SUM(allocated_hours) FILTER
                           (WHERE leave_year=2027 AND allocation_type='PRIOR_YEAR_BALANCE'),0)
                           AS future_carry_hours
                FROM public.leave_document_quota_allocations
                WHERE employee_id=@employee AND leave_type_id=@type
                  AND source_quota_year=2026 AND released_at IS NULL
            )
            SELECT LEAST(96, (24-used_hours) + future_carry_hours + 40) - 48
            FROM source_usage
            """, connection, transaction))
        {
            rolloverCheck.Parameters.AddWithValue("employee", employeeCode);
            rolloverCheck.Parameters.AddWithValue("type", vacationTypeId);
            if (Convert.ToDecimal(await rolloverCheck.ExecuteScalarAsync()) != 16)
                throw new InvalidOperationException("Year rollover double-deduction scenario failed.");
        }

        await using (var cancelCommand = new NpgsqlCommand(
            "UPDATE public.leave_documents SET status='CANCELLED' WHERE id=@id", connection, transaction))
        {
            cancelCommand.Parameters.AddWithValue("id", spillDocument);
            await cancelCommand.ExecuteNonQueryAsync();
        }
        await using (var returnCommand = new NpgsqlCommand("""
            SELECT COALESCE(SUM(allocated_hours),0)
            FROM public.leave_document_quota_allocations
            WHERE leave_document_id=@id AND released_at IS NULL
            """, connection, transaction))
        {
            returnCommand.Parameters.AddWithValue("id", spillDocument);
            if (Convert.ToDecimal(await returnCommand.ExecuteScalarAsync()) != 0)
                throw new InvalidOperationException("Cancelled leave did not return its allocations.");
        }

        await using (var editCommand = new NpgsqlCommand(
            "UPDATE public.leave_documents SET leave_hours=16 WHERE id=@id", connection, transaction))
        {
            editCommand.Parameters.AddWithValue("id", firstDocument);
            await editCommand.ExecuteNonQueryAsync();
        }
        await using (var editCheck = new NpgsqlCommand("""
            SELECT
                COALESCE(SUM(allocated_hours) FILTER (WHERE source_quota_year=2027),0),
                COALESCE(SUM(allocated_hours) FILTER (WHERE source_quota_year=2026),0)
            FROM public.leave_document_quota_allocations
            WHERE leave_document_id=@id AND released_at IS NULL
            """, connection, transaction))
        {
            editCheck.Parameters.AddWithValue("id", firstDocument);
            await using var editReader = await editCheck.ExecuteReaderAsync();
            await editReader.ReadAsync();
            if (editReader.GetDecimal(0) != 8 || editReader.GetDecimal(1) != 8)
                throw new InvalidOperationException("Edited leave was not reallocated to the original quota sources.");
        }

        Console.WriteLine("scenario_new_entitlement_first=True");
        Console.WriteLine("scenario_preemployee_creates_all_initial_quotas=True");
        Console.WriteLine("scenario_preemployee_vacation_starts_at_3_days=True");
        Console.WriteLine("scenario_cancel_returns_source=True");
        Console.WriteLine("scenario_edit_reallocates=True");
        Console.WriteLine("scenario_rollover_does_not_double_deduct=True");

        await using (var cancelFirst = new NpgsqlCommand(
            "UPDATE public.leave_documents SET status='CANCELLED' WHERE id = ANY(@ids)", connection, transaction))
        {
            cancelFirst.Parameters.AddWithValue("ids", scenarioDocuments.ToArray());
            await cancelFirst.ExecuteNonQueryAsync();
        }
        await using (var twentyPlusTwelve = new NpgsqlCommand("""
            UPDATE public.leave_quotas SET quota_hours=160 WHERE employee_id=@employee AND quota_year=2026;
            UPDATE public.leave_quotas SET quota_hours=96,new_entitlement_hours=96,carried_forward_hours=0
             WHERE employee_id=@employee AND quota_year=2027;
            """, connection, transaction))
        {
            twentyPlusTwelve.Parameters.AddWithValue("employee", employeeCode);
            await twentyPlusTwelve.ExecuteNonQueryAsync();
        }
        var capDocument = await InsertDocument($"QV-{Guid.NewGuid():N}"[..30], 8);
        await using (var capCheck = new NpgsqlCommand("""
            SELECT
                COALESCE(SUM(allocated_hours) FILTER (WHERE source_quota_year=2027),0),
                COALESCE(SUM(allocated_hours) FILTER (WHERE source_quota_year=2026),0)
            FROM public.leave_document_quota_allocations
            WHERE leave_document_id=@id AND released_at IS NULL
            """, connection, transaction))
        {
            capCheck.Parameters.AddWithValue("id", capDocument);
            await using var capReader = await capCheck.ExecuteReaderAsync();
            await capReader.ReadAsync();
            if (capReader.GetDecimal(0) != 8 || capReader.GetDecimal(1) != 0)
                throw new InvalidOperationException("20+12 priority scenario failed.");
        }

        await using (var entitlementCheck = new NpgsqlCommand("""
            SELECT public.calculate_vacation_entitlement_hours(@employee, 2028),
                   (SELECT quota_hours FROM public.leave_quotas
                    WHERE employee_id=@employee AND leave_type_id=@type AND quota_year=2026)
            """, connection, transaction))
        {
            entitlementCheck.Parameters.AddWithValue("employee", employeeCode);
            entitlementCheck.Parameters.AddWithValue("type", vacationTypeId);
            await using var entitlementReader = await entitlementCheck.ExecuteReaderAsync();
            await entitlementReader.ReadAsync();
            if (entitlementReader.GetDecimal(0) != 48 || entitlementReader.GetDecimal(1) != 160)
                throw new InvalidOperationException("First-working-day or approved-extra scenario failed.");
        }
        await using (var nonFirstStart = new NpgsqlCommand(
            "UPDATE public.employee_company_info SET start_date=DATE '2026-07-02' WHERE employee_id=@id",
            connection, transaction))
        {
            nonFirstStart.Parameters.AddWithValue("id", employeeId);
            await nonFirstStart.ExecuteNonQueryAsync();
        }
        await using (var nonFirstCheck = new NpgsqlCommand(
            "SELECT public.calculate_vacation_entitlement_hours(@employee, 2028)", connection, transaction))
        {
            nonFirstCheck.Parameters.AddWithValue("employee", employeeCode);
            if (Convert.ToDecimal(await nonFirstCheck.ExecuteScalarAsync()) != 40)
                throw new InvalidOperationException("Non-first-working-day proration scenario failed.");
        }
        Console.WriteLine("scenario_20_plus_12_uses_new_quota_first=True");
        Console.WriteLine("scenario_first_working_day_gets_month=True");
        Console.WriteLine("scenario_non_first_working_day_loses_month=True");
    }
    finally
    {
        await transaction.RollbackAsync();
    }
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

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-personal-documents-tab", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "053_add_employee_personal_documents_permission.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added employee personal documents permission successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-personal-documents", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "054_create_employee_personal_documents.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created employee personal documents storage successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-activity-history", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "055_create_employee_activity_history.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created employee activity history and personal document deletion fields successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-local-authentication", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "056_create_local_authentication.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created local authentication tables successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-local-accounts-page", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "057_add_local_accounts_page.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added local account management page and permissions successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-local-password-management", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "058_add_local_password_management.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added local password management workflow successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-manager-references", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "059_add_employee_manager_references.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added employee manager reference columns successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-request-edit-permission", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "060_add_employee_request_edit_permission.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added employee request-edit permission successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-structured-address", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "061_add_employee_structured_address.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added structured employee address fields successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--import-thailand-address-master", StringComparison.OrdinalIgnoreCase))
{
    const string sourceUrl = "https://raw.githubusercontent.com/open-admin-data/thailand-administrative-divisions/main/data/all-subdistrict.json";
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    var addressJson = await client.GetStringAsync(sourceUrl);
    using var parsedAddress = System.Text.Json.JsonDocument.Parse(addressJson);
    if (parsedAddress.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array ||
        parsedAddress.RootElement.GetArrayLength() < 7000)
        throw new InvalidOperationException("Thailand address source is incomplete.");

    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "062_import_thailand_address_master.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    migrationCommand.CommandTimeout = 300;
    migrationCommand.Parameters.AddWithValue("address_json", NpgsqlTypes.NpgsqlDbType.Jsonb, addressJson);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine($"Imported Thailand address master from {parsedAddress.RootElement.GetArrayLength():N0} subdistrict records successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-local-refresh-revoke-reason", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "063_add_local_refresh_revoke_reason.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added local refresh-token revoke reasons successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-leave-quota-annual-rollover", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "064_create_leave_quota_annual_rollover.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created annual leave quota rollover ledger and report page successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-tax-deductions", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "065_create_employee_tax_deductions.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created yearly employee tax deduction declarations and HR report page successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-lotus-notes-employee-outbox", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "066_create_lotus_notes_employee_outbox.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created Lotus Notes employee outbox and admin permission page successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-lotus-notes-employee-edit-outbox", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "067_extend_lotus_notes_outbox_for_employee_edits.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Extended Lotus Notes outbox for approved employee edit requests successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-attendance-review-email-notification", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "068_add_attendance_review_email_notification_permission.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added attendance review email notification permission successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-workflow-email-notifications", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "069_add_workflow_email_notification_permissions.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added workflow email notification permissions successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-export-permission", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "070_add_employee_export_permission.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Added employee export permission successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-responsibility-provinces", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "071_create_employee_responsibility_provinces.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created employee and Pre-Employee responsibility province relations successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-attendance-response-issues", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "072_create_attendance_response_issues.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created attendance response issue-level review successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-brands", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "073_create_employee_brands.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created Brand master data and employee Brand assignments successfully.");
    return;
}

if (args.Length >= 1 && string.Equals(args[0], "--migrate-employee-comm-groups", StringComparison.OrdinalIgnoreCase))
{
    var migrationPath = Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..", "database", "Scripts", "074_create_employee_comm_groups.sql"));
    var migrationSql = await File.ReadAllTextAsync(migrationPath);
    await using var migrationDataSource = NpgsqlDataSource.Create(connectionString);
    await using var migrationCommand = migrationDataSource.CreateCommand(migrationSql);
    await migrationCommand.ExecuteNonQueryAsync();
    Console.WriteLine("Created Comm.Group master data and employee Comm.Group assignments successfully.");
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
builder.Services.AddSingleton<LeaveQuotaAnnualRolloverService>();
builder.Services.AddHostedService<LeaveQuotaAnnualRolloverWorker>();
builder.Services.AddSingleton<LocalJwtService>();
builder.Services.AddSingleton<IPasswordHasher<HrProject.Api.Controllers.LocalAuthenticationController.LocalUser>,
    PasswordHasher<HrProject.Api.Controllers.LocalAuthenticationController.LocalUser>>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<MicrosoftGraphMailService>();
builder.Services.AddSingleton<LeaveApprovalEmailService>();
builder.Services.AddSingleton<LeaveDecisionEmailService>();
builder.Services.AddSingleton<LeaveCancellationEmailService>();
builder.Services.AddSingleton<AttendanceReviewEmailNotificationService>();
builder.Services.AddSingleton<WorkflowEmailNotificationService>();
builder.Services.AddSingleton<OutlookCalendarSyncService>();
builder.Services.AddHostedService<OutlookCalendarRetryWorker>();
builder.Services.AddSingleton<CompanyCalendarOutlookSyncService>();
builder.Services.AddHostedService<CompanyCalendarOutlookRetryWorker>();
builder.Services.AddSingleton<AttendanceEventOutlookSyncService>();
builder.Services.AddHostedService<AttendanceEventOutlookRetryWorker>();
builder.Services.AddSingleton<LeaveCommentEmailService>();
builder.Services.AddHostedService<LeaveCommentEmailRetryWorker>();
builder.Services.AddSingleton<LotusNotesOutboxSignal>();
builder.Services.AddHostedService<LotusNotesEmployeeOutboxWorker>();
var tenantId = builder.Configuration["AzureAd:TenantId"]
    ?? throw new InvalidOperationException("AzureAd:TenantId is not configured.");
var clientId = builder.Configuration["AzureAd:ClientId"]
    ?? throw new InvalidOperationException("AzureAd:ClientId is not configured.");
var requiredScope = builder.Configuration["AzureAd:Scope"] ?? "users.read";

const string entraScheme = "EntraBearer";
var localSigningKey = LocalJwtService.GetSigningKey(builder.Configuration);
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = entraScheme;
        options.DefaultChallengeScheme = entraScheme;
    })
    .AddJwtBearer(entraScheme, options =>
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
    })
    .AddJwtBearer(LocalJwtService.AuthenticationScheme, options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = LocalJwtService.Issuer,
            ValidateAudience = true,
            ValidAudience = LocalJwtService.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(localSigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name"
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("HrApiScope", policy =>
    {
        policy.AddAuthenticationSchemes(entraScheme, LocalJwtService.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            var scopes = context.User.FindFirst("scp")?.Value?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            return context.User.HasClaim("auth_source", "LOCAL") ||
                   scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase) ||
                   context.User.HasClaim("roles", "Users.Read");
        });
    });
    options.FallbackPolicy = new AuthorizationPolicyBuilder(
            entraScheme, LocalJwtService.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireAssertion(context =>
        {
            var scopes = context.User.FindFirst("scp")?.Value?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            return context.User.HasClaim("auth_source", "LOCAL") ||
                   scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase) ||
                   context.User.HasClaim("roles", "Users.Read");
        })
        .Build();
});
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("https://localhost:7169", "http://localhost:5043")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

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
app.UseResponseCompression();
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
