namespace HrProject.AttendanceWorker;

public sealed class WifiAttendanceOptions
{
    public bool Enabled { get; set; } = true;
    public int InitialLookbackDays { get; set; } = 45;
    public int BatchSize { get; set; } = 5000;
    public int OverlapHours { get; set; } = 24;
    public string? SourceSchema { get; set; }
    public string? SourceTable { get; set; }
}
