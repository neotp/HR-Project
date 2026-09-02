namespace HrProject.AttendanceWorker;

public sealed class AttendanceWorkerOptions
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 60;
    public int InitialLookbackDays { get; set; } = 45;
    public int BatchSize { get; set; } = 5000;
    public string? SourceSchema { get; set; }
    public string? SourceTable { get; set; }
    public string TimeZone { get; set; } = "SE Asia Standard Time";
}
