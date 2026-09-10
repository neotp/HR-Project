using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace HrProject.Api.Services;

public sealed class LocalJwtService(IConfiguration configuration)
{
    public const string AuthenticationScheme = "LocalBearer";
    public const string Issuer = "HrProject.Api.Local";
    public const string Audience = "HrProject.Client";

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(
        Guid localUserId, string employeeId, string employeeName, string email,
        bool mustChangePassword)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(
            Math.Clamp(configuration.GetValue("LocalJwt:AccessTokenMinutes", 15), 5, 60));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, localUserId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("employee_id", employeeId),
            new("name", employeeName),
            new("email", email),
            new("auth_source", "LOCAL"),
            new("must_change_password", mustChangePassword ? "true" : "false")
        };
        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(GetSigningKey(configuration))),
                SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public static string GetSigningKey(IConfiguration configuration)
    {
        var value = configuration["LocalJwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(value) || value.Length < 32)
            throw new InvalidOperationException(
                "LocalJwt:SigningKey must be configured and contain at least 32 characters.");
        return value;
    }

    public static string CreateRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    public static string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
