namespace HrProject.Api.Services;

public static class MaternityLeavePolicy
{
    public const string FemaleTypeCode = "UNPAID";
    public const string MaleTypeCode = "PATERNITY";

    public static bool IsMaternityType(string? code) =>
        code is FemaleTypeCode or MaleTypeCode;

    public static string? AllowedTypeCode(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var normalized = new string(title.Trim().Where(character =>
            !char.IsWhiteSpace(character) && character != '.').ToArray()).ToLowerInvariant();
        return normalized switch
        {
            "นาย" or "mr" => MaleTypeCode,
            "นาง" or "นางสาว" or "นส" or "mrs" or "ms" or "miss" => FemaleTypeCode,
            _ => null
        };
    }

    public static bool AreConsecutiveFullDays(IReadOnlyList<(DateOnly Date, decimal Hours)> items)
    {
        if (items.Count == 0) return false;
        var ordered = items.OrderBy(item => item.Date).ToArray();
        return ordered.All(item => item.Hours == 8) &&
            ordered.Skip(1).Select((item, index) => item.Date.DayNumber - ordered[index].Date.DayNumber)
                .All(gap => gap == 1);
    }
}
