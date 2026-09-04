namespace HrProject.Shared.Models;

public static class EmployeeCodeFormat
{
    public const int Length = 6;

    public static bool IsValid(string? value) =>
        value is not null && value.Trim() is { Length: Length } code && code.All(char.IsDigit);

    public static string Display(string? value)
    {
        var code = value?.Trim() ?? string.Empty;
        return code.Length > 0 && code.Length < Length && code.All(char.IsDigit)
            ? code.PadLeft(Length, '0')
            : code;
    }

    public static string NormalizeNew(string? value) => Display(value);
}
