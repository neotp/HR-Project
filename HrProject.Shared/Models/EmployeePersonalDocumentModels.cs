namespace HrProject.Shared.Models;

public sealed record EmployeePersonalDocumentDto(
    long Id,
    int EmployeeId,
    string FileName,
    string ContentType,
    long FileSizeBytes,
    string UploadedBy,
    string UploadedByName,
    DateTimeOffset UploadedAt);

public sealed record EmployeePersonalDocumentUploadDto(
    string FileName,
    string ContentType,
    byte[] Content);

public sealed record AddEmployeePersonalDocumentsRequest(
    IReadOnlyList<EmployeePersonalDocumentUploadDto> Attachments);
