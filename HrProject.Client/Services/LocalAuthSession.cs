using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Claims;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace HrProject.Client.Services;

public sealed class LocalAuthSession(IHttpClientFactory httpClientFactory)
{
    private readonly SemaphoreSlim restoreLock = new(1, 1);
    private string? accessToken;
    private DateTimeOffset expiresAt;
    private CurrentMicrosoftUserDto? currentUser;
    private string? username;
    private bool mustChangePassword;
    private bool restoreAttempted;

    public event Action? StateChanged;
    public bool IsAuthenticated =>
        currentUser is not null && !string.IsNullOrWhiteSpace(accessToken) &&
        expiresAt > DateTimeOffset.UtcNow.AddSeconds(30);
    public CurrentMicrosoftUserDto? CurrentUser => currentUser;
    public string? Username => username;
    public bool MustChangePassword => mustChangePassword;

    public async Task<LocalAuthTokenDto> LoginAsync(
        string username, string password, CancellationToken cancellationToken = default)
    {
        var response = await SendWithCredentials(
            HttpMethod.Post, "api/local-auth/login",
            JsonContent.Create(new LocalLoginRequest(username, password)), cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ReadError(response));

        var session = await response.Content.ReadFromJsonAsync<LocalAuthTokenDto>(cancellationToken)
            ?? throw new InvalidOperationException("ระบบไม่ได้ส่งข้อมูลการเข้าสู่ระบบกลับมา");
        Apply(session);
        restoreAttempted = true;
        StateChanged?.Invoke();
        return session;
    }

    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        if (IsAuthenticated) return true;
        await restoreLock.WaitAsync(cancellationToken);
        try
        {
            if (IsAuthenticated) return true;
            if (restoreAttempted) return false;
            restoreAttempted = true;
            try
            {
                var response = await SendWithCredentials(
                    HttpMethod.Post, "api/local-auth/refresh", null, cancellationToken);
                if (!response.IsSuccessStatusCode) return false;
                var session = await response.Content.ReadFromJsonAsync<LocalAuthTokenDto>(cancellationToken);
                if (session is null) return false;
                Apply(session);
                StateChanged?.Invoke();
                return true;
            }
            catch
            {
                return false;
            }
        }
        finally
        {
            restoreLock.Release();
        }
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (IsAuthenticated) return accessToken;
        if (currentUser is not null)
            restoreAttempted = false;
        await TryRestoreAsync(cancellationToken);
        return IsAuthenticated ? accessToken : null;
    }

    public void InvalidateAccessToken(string failedToken)
    {
        if (!string.Equals(accessToken, failedToken, StringComparison.Ordinal)) return;
        expiresAt = default;
        restoreAttempted = false;
    }

    public ClaimsPrincipal CreatePrincipal()
    {
        if (currentUser is null) return new ClaimsPrincipal(new ClaimsIdentity());
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, currentUser.EmployeeId),
            new Claim(ClaimTypes.Name, currentUser.EmployeeName),
            new Claim("name", currentUser.EmployeeName),
            new Claim("employee_id", currentUser.EmployeeId),
            new Claim("email", currentUser.Email),
            new Claim("auth_source", "LOCAL"),
            new Claim("must_change_password", mustChangePassword ? "true" : "false")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Local"));
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SendWithCredentials(HttpMethod.Post, "api/local-auth/logout", null, cancellationToken);
        }
        finally
        {
            accessToken = null;
            currentUser = null;
            username = null;
            mustChangePassword = false;
            expiresAt = default;
            restoreAttempted = true;
            StateChanged?.Invoke();
        }
    }

    public async Task ChangePasswordAsync(string currentPassword, string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("กรุณาเข้าสู่ระบบใหม่");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/local-auth/change-password")
        {
            Content = JsonContent.Create(new LocalChangePasswordRequest(currentPassword, newPassword))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await httpClientFactory.CreateClient("HrApiAnonymous")
            .SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ReadError(response));
        var session = await response.Content.ReadFromJsonAsync<LocalAuthTokenDto>(cancellationToken)
            ?? throw new InvalidOperationException("ระบบไม่ได้ส่งข้อมูลบัญชีกลับมา");
        Apply(session);
        StateChanged?.Invoke();
    }

    public async Task RequestPasswordResetAsync(string usernameOrEmployeeId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendWithCredentials(HttpMethod.Post, "api/local-auth/forgot-password",
            JsonContent.Create(new LocalForgotPasswordRequest(usernameOrEmployeeId)), cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ReadError(response));
    }

    private void Apply(LocalAuthTokenDto session)
    {
        accessToken = session.AccessToken;
        expiresAt = session.ExpiresAt;
        currentUser = session.User;
        username = session.Username;
        mustChangePassword = session.MustChangePassword;
    }

    private async Task<HttpResponseMessage> SendWithCredentials(
        HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        return await httpClientFactory.CreateClient("HrApiAnonymous")
            .SendAsync(request, cancellationToken);
    }

    private static async Task<string> ReadError(HttpResponseMessage response)
    {
        var body = (await response.Content.ReadAsStringAsync()).Trim().Trim('"');
        return string.IsNullOrWhiteSpace(body)
            ? $"เข้าสู่ระบบไม่สำเร็จ ({(int)response.StatusCode})"
            : body;
    }
}
