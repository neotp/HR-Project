using Microsoft.Extensions.Configuration;

const string userSecretsId = "HrProject.Api-LocalDevelopment";

try
{
    var dryRun = args.Any(argument => string.Equals(argument, "--dry-run", StringComparison.OrdinalIgnoreCase));
    var filePath = GetArgument(args, "--file") ?? args.FirstOrDefault(argument => !argument.StartsWith("--"));
    if (string.IsNullOrWhiteSpace(filePath))
        throw new ArgumentException("กรุณาระบุไฟล์ด้วย --file <path>");

    filePath = Path.GetFullPath(filePath);
    var apiDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "HrProject.Api"));
    if (!Directory.Exists(apiDirectory))
        apiDirectory = Directory.GetCurrentDirectory();

    var connectionString = string.Empty;
    if (!dryRun)
    {
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Development";
        var configuration = new ConfigurationBuilder()
            .SetBasePath(apiDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .AddUserSecrets(userSecretsId)
            .AddEnvironmentVariables()
            .Build();
        connectionString = configuration.GetConnectionString("HrDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "ไม่พบ ConnectionStrings:HrDatabase กรุณาตั้ง User Secret หรือ Environment Variable ConnectionStrings__HrDatabase");
    }

    var result = await JobApplicationImporter.ImportAsync(connectionString, filePath, dryRun);
    Console.WriteLine(
        $"{(dryRun ? "DRY_RUN_OK" : "IMPORT_OK")} file={Path.GetFileName(filePath)} rows={result.TotalRows} inserted={result.Inserted} skipped={result.Skipped} invalid={result.Invalid}");
    return 0;
}
catch (FileNotFoundException exception)
{
    Console.Error.WriteLine($"IMPORT_FILE_NOT_FOUND {exception.FileName}");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"IMPORT_FAILED {exception.Message}");
    return 1;
}

static string? GetArgument(string[] values, string name)
{
    for (var index = 0; index < values.Length - 1; index++)
    {
        if (string.Equals(values[index], name, StringComparison.OrdinalIgnoreCase))
            return values[index + 1];
    }

    return null;
}
