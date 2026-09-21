namespace HrProject.Shared.Models;

public sealed record EmployeeRecruitDocumentLinkDto(
    long Id,
    int EmployeeId,
    string Url,
    string AddedBy,
    string AddedByName,
    DateTimeOffset AddedAt);

public sealed record AddEmployeeRecruitDocumentLinkRequest(string Url);
