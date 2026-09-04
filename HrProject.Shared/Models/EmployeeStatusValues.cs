namespace HrProject.Shared.Models;

public static class EmployeeStatusValues
{
    public const string Employee = "พนักงาน";
    public const string Resigned = "ลาออก";

    public static bool IsValid(string? value) =>
        value is Employee or Resigned;

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Resigned, StringComparison.Ordinal) ? Resigned : Employee;
}
