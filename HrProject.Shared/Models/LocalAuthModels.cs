namespace HrProject.Shared.Models;

public sealed record LocalLoginRequest(string Username, string Password);

public sealed record LocalAuthTokenDto(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    CurrentMicrosoftUserDto User,
    string Username,
    bool MustChangePassword);

public sealed record CreateLocalAccountRequest(
    string EmployeeId,
    string Username);

public sealed record LocalAccountDto(
    string EmployeeId,
    string EmployeeName,
    string Username,
    bool IsActive,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt);

public sealed record SaveLocalAccountStatusRequest(bool IsActive);

public sealed record LocalChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);

public sealed record LocalForgotPasswordRequest(string UsernameOrEmployeeId);

public sealed record LocalPasswordResetRequestDto(
    long Id,
    string EmployeeId,
    string EmployeeName,
    string Username,
    DateTimeOffset RequestedAt);
