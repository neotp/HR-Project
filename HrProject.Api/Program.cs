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
    var imported = await EmployeeWorkbookImporter.ImportAsync(
        connectionString, workbookPath, migrationPath);
    Console.WriteLine($"Imported {imported} employee rows successfully.");
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

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<PageActionPermissionService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<MicrosoftGraphMailService>();
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
