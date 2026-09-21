using System.Text.Json;

namespace HrProject.Shared.Models;

public sealed record LotusNotesSyncItemDto(
    long Id,
    long? PreEmployeeId,
    long? EmployeeEditRequestId,
    long EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string DatabaseName,
    string ExternalKey,
    JsonElement Payload,
    string Status,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? SucceededAt,
    string? LastError,
    string? ResponseSummary,
    DateTimeOffset? RetryRequestedAt,
    string? RetryRequestedByName);

public sealed record LotusNotesSyncSummaryDto(
    int Pending,
    int Processing,
    int Success,
    int Failed);
