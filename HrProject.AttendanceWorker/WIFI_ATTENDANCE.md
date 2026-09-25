# Wi-Fi attendance source

Run the HR database migration before starting the updated worker:

```powershell
dotnet run --project HrProject.Api -- --migrate-attendance-wifi
```

Configure `ConnectionStrings:WifiAttendanceDatabase` through an environment variable or user secrets. Do not put the password in a committed JSON file. For an environment variable, use `ConnectionStrings__WifiAttendanceDatabase`.

The importer can find the source PostgreSQL table when exactly one visible table has `calling_station_id` and `start_time`. If more than one does, set `WifiAttendance:SourceSchema` and `WifiAttendance:SourceTable`. The table also needs `session_id`. The database login needs read access to that table only.

The importer reads only MAC addresses assigned to active, attendance-eligible employees. It normalizes MAC formatting, ignores invalid or duplicate employee assignments, and stores the original `start_time` as `timestamptz`. The attendance worker converts it to `Asia/Bangkok` when selecting the work date and compares it with the first camera scan. Wi-Fi is used only when its connection started earlier; camera scans remain the source of the departure time. A Wi-Fi-only record has no departure time.

Source rows are re-read over the configured overlap window and deduplicated in the HR database. `attendance_wifi_sync_state` records the last successfully scanned window and errors. Restart the attendance worker after configuring the source connection.
