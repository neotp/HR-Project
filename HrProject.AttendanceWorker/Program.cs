using HrProject.AttendanceWorker;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
var hrConnectionString = builder.Configuration.GetConnectionString("HrDatabase")
    ?? throw new InvalidOperationException("Connection string 'HrDatabase' is not configured.");
var settings = new NpgsqlConnectionStringBuilder(hrConnectionString);
if (settings.KeepAlive == 0) settings.KeepAlive = 30;

builder.Services.Configure<AttendanceWorkerOptions>(builder.Configuration.GetSection("AttendanceWorker"));
builder.Services.AddSingleton(NpgsqlDataSource.Create(settings.ConnectionString));
builder.Services.AddSingleton<AttendanceProcessor>();
builder.Services.AddHostedService<HikvisionAttendanceWorker>();

await builder.Build().RunAsync();
