using HrProject.Api.Services;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/local-auth")]
public sealed class LocalAuthenticationController(
    NpgsqlDataSource dataSource,
    IPasswordHasher<LocalAuthenticationController.LocalUser> passwordHasher,
    LocalJwtService jwtService,
    PageActionPermissionService actionPermissionService,
    PageAccessService pageAccessService,
    MicrosoftGraphMailService graphMailService,
    IWebHostEnvironment environment,
    ILogger<LocalAuthenticationController> logger) : ControllerBase
{
    private const string RefreshCookieName = "hr_local_refresh";
    private const string DefaultPassword = "P@ssw0rdSiS";

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<LocalAuthTokenDto>> Login(
        LocalLoginRequest request, CancellationToken cancellationToken)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest("กรุณากรอกชื่อผู้ใช้และรหัสผ่าน");

        var user = await FindUserByUsername(username, cancellationToken);
        if (user is null)
        {
            await WriteAudit(null, username, null, false, "USER_NOT_FOUND", cancellationToken);
            return Unauthorized("ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง");
        }
        if (!user.IsActive || !user.EmployeeActive || user.EmployeeStatus == "ลาออก")
        {
            await WriteAudit(user.Id, username, user.EmployeeId, false, "ACCOUNT_DISABLED", cancellationToken);
            return Unauthorized("บัญชีนี้ถูกปิดใช้งาน");
        }
        if (user.LockoutEnd > DateTimeOffset.UtcNow)
        {
            await WriteAudit(user.Id, username, user.EmployeeId, false, "LOCKED_OUT", cancellationToken);
            return StatusCode(StatusCodes.Status423Locked,
                $"บัญชีถูกล็อกชั่วคราว กรุณาลองใหม่หลัง {user.LockoutEnd.Value.ToLocalTime():dd/MM/yyyy HH:mm}");
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            await RegisterFailedAttempt(user.Id, cancellationToken);
            await WriteAudit(user.Id, username, user.EmployeeId, false, "INVALID_PASSWORD", cancellationToken);
            return Unauthorized("ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง");
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            await UpdatePasswordHash(user.Id, passwordHasher.HashPassword(user, request.Password), cancellationToken);

        await RegisterSuccessfulLogin(user.Id, cancellationToken);
        await WriteAudit(user.Id, username, user.EmployeeId, true, null, cancellationToken);
        return Ok(await CreateSession(user, cancellationToken));
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<ActionResult<LocalAuthTokenDto>> Refresh(CancellationToken cancellationToken)
    {
        if (!Request.Cookies.TryGetValue(RefreshCookieName, out var rawToken) ||
            string.IsNullOrWhiteSpace(rawToken))
            return Unauthorized();

        var tokenHash = LocalJwtService.HashRefreshToken(rawToken);
        const string sql = """
            SELECT account.id, account.employee_id, account.username, account.password_hash,
                   account.is_active, account.must_change_password, account.failed_access_count,
                   account.lockout_end, employee.is_active,
                   COALESCE(company.employee_status, ''),
                   COALESCE(NULLIF(basic.full_name_th, ''), NULLIF(basic.full_name_en, ''), employee.employee_code),
                   COALESCE(NULLIF(basic.email_address, ''), '')
            FROM public.local_refresh_tokens token
            JOIN public.local_user_accounts account ON account.id = token.local_user_id
            JOIN public.employees employee ON employee.employee_code = account.employee_id
            LEFT JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
            LEFT JOIN public.employee_company_info company ON company.employee_id = employee.id
            WHERE token.token_hash = @token_hash
              AND (token.revoked_at IS NULL OR
                   (token.revoke_reason = 'ROTATED' AND
                    token.revoked_at > CURRENT_TIMESTAMP - INTERVAL '30 seconds'))
              AND token.expires_at > CURRENT_TIMESTAMP
            LIMIT 1
            """;
        LocalUser? user = null;
        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("token_hash", tokenHash);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) user = ReadUser(reader);
        }
        if (user is null || !user.IsActive || !user.EmployeeActive || user.EmployeeStatus == "ลาออก")
        {
            return Unauthorized();
        }

        await RevokeRefreshToken(tokenHash, "ROTATED", cancellationToken);
        return Ok(await CreateSession(user, cancellationToken));
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (Request.Cookies.TryGetValue(RefreshCookieName, out var token) &&
            !string.IsNullOrWhiteSpace(token))
            await RevokeRefreshToken(LocalJwtService.HashRefreshToken(token), "LOGOUT", cancellationToken);
        DeleteRefreshCookie();
        return NoContent();
    }

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(
        LocalForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        var lookup = request.UsernameOrEmployeeId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(lookup))
        {
            const string sql = """
                INSERT INTO public.local_password_reset_requests
                    (local_user_id, employee_id, requested_ip)
                SELECT account.id, account.employee_id, @ip
                FROM public.local_user_accounts account
                WHERE account.is_active=TRUE
                  AND (account.normalized_username=@lookup OR UPPER(account.employee_id)=@lookup)
                  AND NOT EXISTS
                  (
                      SELECT 1 FROM public.local_password_reset_requests pending
                      WHERE pending.local_user_id=account.id AND pending.status='PENDING'
                  )
                """;
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("lookup", lookup.ToUpperInvariant());
            command.Parameters.AddWithValue("ip", ClientIp);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return Ok("หากพบข้อมูลบัญชี ระบบได้ส่งคำขอรีเซ็ตรหัสผ่านเรียบร้อยแล้ว");
    }

    [Authorize(Policy = "HrApiScope")]
    [HttpPost("change-password")]
    public async Task<ActionResult<LocalAuthTokenDto>> ChangePassword(
        LocalChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (!User.HasClaim("auth_source", "LOCAL")) return Forbid();
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
            string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
            return BadRequest("รหัสผ่านใหม่ต้องมีอย่างน้อย 8 ตัวอักษร");
        if (request.NewPassword == DefaultPassword)
            return BadRequest("กรุณากำหนดรหัสผ่านใหม่ที่ไม่ใช่รหัสผ่านเริ่มต้น");

        var employeeId = User.FindFirst("employee_id")?.Value;
        if (string.IsNullOrWhiteSpace(employeeId)) return Forbid();
        var user = await FindUserByEmployeeId(employeeId, cancellationToken);
        if (user is null) return Forbid();
        if (passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) ==
            PasswordVerificationResult.Failed)
            return BadRequest("รหัสผ่านปัจจุบันไม่ถูกต้อง");

        var hash = passwordHasher.HashPassword(user, request.NewPassword);
        await using (var command = dataSource.CreateCommand("""
            UPDATE public.local_user_accounts
            SET password_hash=@hash, must_change_password=FALSE,
                password_changed_at=CURRENT_TIMESTAMP, security_stamp=gen_random_uuid(),
                failed_access_count=0, lockout_end=NULL, updated_at=CURRENT_TIMESTAMP
            WHERE id=@id
            """))
        {
            command.Parameters.AddWithValue("id", user.Id);
            command.Parameters.AddWithValue("hash", hash);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await RevokeAllRefreshTokens(user.Id, cancellationToken);
        user = user with { PasswordHash = hash, MustChangePassword = false };
        return Ok(await CreateSession(user, cancellationToken));
    }

    [Authorize(Policy = "HrApiScope")]
    [HttpGet("accounts")]
    public async Task<ActionResult<IReadOnlyList<LocalAccountDto>>> GetAccounts(
        CancellationToken cancellationToken)
    {
        if (!await HasManagementPermission("VIEW_ACCOUNTS", cancellationToken))
            return Forbid();

        const string sql = """
            SELECT account.employee_id,
                   COALESCE(NULLIF(basic.full_name_th, ''), NULLIF(basic.full_name_en, ''), account.employee_id),
                   account.username, account.is_active, account.last_login_at, account.created_at
            FROM public.local_user_accounts account
            JOIN public.employees employee ON employee.employee_code = account.employee_id
            LEFT JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
            ORDER BY account.employee_id
            """;
        var accounts = new List<LocalAccountDto>();
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            accounts.Add(new LocalAccountDto(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }
        return Ok(accounts);
    }

    [Authorize(Policy = "HrApiScope")]
    [HttpPost("accounts")]
    public async Task<IActionResult> CreateOrResetAccount(
        CreateLocalAccountRequest request, CancellationToken cancellationToken)
    {
        var actor = await ResolveActorEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actor))
            return Forbid();

        var employeeId = request.EmployeeId?.Trim() ?? string.Empty;
        var username = request.Username?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(employeeId) || string.IsNullOrWhiteSpace(username))
            return BadRequest("กรุณาระบุพนักงานและชื่อผู้ใช้");

        var exists = false;
        await using (var existsCommand = dataSource.CreateCommand(
            "SELECT EXISTS(SELECT 1 FROM public.local_user_accounts WHERE employee_id=@employee_id)"))
        {
            existsCommand.Parameters.AddWithValue("employee_id", employeeId);
            exists = (bool?)await existsCommand.ExecuteScalarAsync(cancellationToken) ?? false;
            var requiredAction = exists ? "RESET_PASSWORD" : "CREATE_ACCOUNT";
            if (!await HasManagementPermission(requiredAction, cancellationToken))
                return Forbid();
        }

        var subject = new LocalUser(Guid.Empty, employeeId, username, string.Empty, true,
            true, 0, null, true, string.Empty, string.Empty, string.Empty);
        var hash = passwordHasher.HashPassword(subject, DefaultPassword);
        const string sql = """
            INSERT INTO public.local_user_accounts
                (employee_id, username, normalized_username, password_hash,
                 must_change_password, created_by)
            SELECT employee_code, @username, @normalized_username, @password_hash,
                   TRUE, @created_by
            FROM public.employees
            WHERE employee_code = @employee_id AND is_active = TRUE
            ON CONFLICT (employee_id) DO UPDATE SET
                username = EXCLUDED.username,
                normalized_username = EXCLUDED.normalized_username,
                password_hash = EXCLUDED.password_hash,
                is_active = TRUE,
                must_change_password = TRUE,
                failed_access_count = 0,
                lockout_end = NULL,
                security_stamp = gen_random_uuid(),
                updated_at = CURRENT_TIMESTAMP
            """;
        try
        {
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("employee_id", employeeId);
            command.Parameters.AddWithValue("username", username);
            command.Parameters.AddWithValue("normalized_username", username.ToUpperInvariant());
            command.Parameters.AddWithValue("password_hash", hash);
            command.Parameters.AddWithValue("created_by", actor);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                return NotFound("ไม่พบพนักงานที่กำหนด");

            var account = await FindUserByEmployeeId(employeeId, cancellationToken);
            if (account is not null)
            {
                await RevokeAllRefreshTokens(account.Id, cancellationToken);
                await CompleteResetRequests(account.Id, actor, cancellationToken);
                await SendPasswordResetEmailSafely(actor, account, cancellationToken);
            }
            return NoContent();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Conflict("ชื่อผู้ใช้นี้ถูกใช้งานแล้ว");
        }
    }

    [Authorize(Policy = "HrApiScope")]
    [HttpGet("password-reset-requests")]
    public async Task<ActionResult<IReadOnlyList<LocalPasswordResetRequestDto>>> GetPasswordResetRequests(
        CancellationToken cancellationToken)
    {
        if (!await HasManagementPermission("RESET_PASSWORD", cancellationToken)) return Forbid();
        const string sql = """
            SELECT request.id, request.employee_id,
                   COALESCE(NULLIF(basic.full_name_th, ''), NULLIF(basic.full_name_en, ''), request.employee_id),
                   account.username, request.requested_at
            FROM public.local_password_reset_requests request
            JOIN public.local_user_accounts account ON account.id=request.local_user_id
            JOIN public.employees employee ON employee.employee_code=request.employee_id
            LEFT JOIN public.employee_basic_info basic ON basic.employee_id=employee.id
            WHERE request.status='PENDING'
            ORDER BY request.requested_at
            """;
        var result = new List<LocalPasswordResetRequestDto>();
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        return Ok(result);
    }

    [Authorize(Policy = "HrApiScope")]
    [HttpPut("accounts/{employeeId}/status")]
    public async Task<IActionResult> SaveAccountStatus(
        string employeeId,
        SaveLocalAccountStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (!await HasManagementPermission("ENABLE_DISABLE_ACCOUNT", cancellationToken))
            return Forbid();

        const string sql = """
            UPDATE public.local_user_accounts
            SET is_active=@is_active, failed_access_count=0, lockout_end=NULL,
                security_stamp=gen_random_uuid(), updated_at=CURRENT_TIMESTAMP
            WHERE employee_id=@employee_id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", employeeId.Trim());
        command.Parameters.AddWithValue("is_active", request.IsActive);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            return NotFound("ไม่พบบัญชี Local ของพนักงานนี้");

        if (!request.IsActive)
        {
            await using var revokeCommand = dataSource.CreateCommand("""
                UPDATE public.local_refresh_tokens token
                SET revoked_at=CURRENT_TIMESTAMP
                FROM public.local_user_accounts account
                WHERE token.local_user_id=account.id AND account.employee_id=@employee_id
                  AND token.revoked_at IS NULL
                """);
            revokeCommand.Parameters.AddWithValue("employee_id", employeeId.Trim());
            await revokeCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        return NoContent();
    }

    private async Task<LocalAuthTokenDto> CreateSession(LocalUser user, CancellationToken token)
    {
        var access = jwtService.CreateAccessToken(user.Id, user.EmployeeId, user.EmployeeName, user.Email,
            user.MustChangePassword);
        var refreshToken = LocalJwtService.CreateRefreshToken();
        var refreshHash = LocalJwtService.HashRefreshToken(refreshToken);
        const string sql = """
            INSERT INTO public.local_refresh_tokens
                (local_user_id, token_hash, expires_at, created_ip)
            VALUES (@user_id, @token_hash, CURRENT_TIMESTAMP + INTERVAL '12 hours', @ip)
            """;
        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("user_id", user.Id);
            command.Parameters.AddWithValue("token_hash", refreshHash);
            command.Parameters.AddWithValue("ip", ClientIp);
            await command.ExecuteNonQueryAsync(token);
        }
        Response.Cookies.Append(RefreshCookieName, refreshToken, CookieOptions());
        var employee = await LoadCurrentUser(user.EmployeeId, user.Id, token);
        return new LocalAuthTokenDto(access.Token, access.ExpiresAt, employee, user.Username,
            user.MustChangePassword);
    }

    private async Task<CurrentMicrosoftUserDto> LoadCurrentUser(
        string employeeId, Guid localUserId, CancellationToken token)
    {
        const string sql = """
            SELECT COALESCE(NULLIF(b.email_address, ''), ''),
                   COALESCE(NULLIF(b.full_name_th, ''), NULLIF(b.full_name_en, ''), e.employee_code),
                   COALESCE(c.department, ''), COALESCE(c.position_name, ''),
                   COALESCE(c.supervisor_name, ''), COALESCE(c.leave_approver_name, '')
            FROM public.employees e
            LEFT JOIN public.employee_basic_info b ON b.employee_id = e.id
            LEFT JOIN public.employee_company_info c ON c.employee_id = e.id
            WHERE e.employee_code = @employee_id
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", employeeId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Employee was not found.");
        return new CurrentMicrosoftUserDto("LOCAL", localUserId.ToString(), reader.GetString(0),
            reader.GetString(1), employeeId, reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), true);
    }

    private async Task<LocalUser?> FindUserByUsername(string username, CancellationToken token)
    {
        const string sql = """
            SELECT account.id, account.employee_id, account.username, account.password_hash,
                   account.is_active, account.must_change_password, account.failed_access_count,
                   account.lockout_end, employee.is_active,
                   COALESCE(company.employee_status, ''),
                   COALESCE(NULLIF(basic.full_name_th, ''), NULLIF(basic.full_name_en, ''), employee.employee_code),
                   COALESCE(NULLIF(basic.email_address, ''), '')
            FROM public.local_user_accounts account
            JOIN public.employees employee ON employee.employee_code = account.employee_id
            LEFT JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
            LEFT JOIN public.employee_company_info company ON company.employee_id = employee.id
            WHERE account.normalized_username = @username
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("username", username.ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadUser(reader) : null;
    }

    private async Task<LocalUser?> FindUserByEmployeeId(string employeeId, CancellationToken token)
    {
        const string sql = """
            SELECT account.id, account.employee_id, account.username, account.password_hash,
                   account.is_active, account.must_change_password, account.failed_access_count,
                   account.lockout_end, employee.is_active,
                   COALESCE(company.employee_status, ''),
                   COALESCE(NULLIF(basic.full_name_th, ''), NULLIF(basic.full_name_en, ''), employee.employee_code),
                   COALESCE(NULLIF(basic.email_address, ''), '')
            FROM public.local_user_accounts account
            JOIN public.employees employee ON employee.employee_code=account.employee_id
            LEFT JOIN public.employee_basic_info basic ON basic.employee_id=employee.id
            LEFT JOIN public.employee_company_info company ON company.employee_id=employee.id
            WHERE account.employee_id=@employee_id LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", employeeId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadUser(reader) : null;
    }

    private static LocalUser ReadUser(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetBoolean(4), reader.GetBoolean(5), reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), reader.GetBoolean(8),
        reader.GetString(9), reader.GetString(10), reader.GetString(11));

    private async Task RegisterFailedAttempt(Guid id, CancellationToken token)
    {
        const string sql = """
            UPDATE public.local_user_accounts
            SET failed_access_count = failed_access_count + 1,
                lockout_end = CASE WHEN failed_access_count + 1 >= 5
                                   THEN CURRENT_TIMESTAMP + INTERVAL '15 minutes'
                                   ELSE lockout_end END,
                updated_at = CURRENT_TIMESTAMP
            WHERE id = @id
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task RegisterSuccessfulLogin(Guid id, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.local_user_accounts
            SET failed_access_count = 0, lockout_end = NULL,
                last_login_at = CURRENT_TIMESTAMP, updated_at = CURRENT_TIMESTAMP
            WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task UpdatePasswordHash(Guid id, string hash, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE public.local_user_accounts SET password_hash=@hash, updated_at=CURRENT_TIMESTAMP WHERE id=@id");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("hash", hash);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task RevokeRefreshToken(string hash, string reason, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.local_refresh_tokens
            SET revoked_at = CURRENT_TIMESTAMP, revoked_ip = @ip, revoke_reason = @reason
            WHERE token_hash = @hash AND revoked_at IS NULL
            """);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("ip", ClientIp);
        command.Parameters.AddWithValue("reason", reason);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task RevokeAllRefreshTokens(Guid userId, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.local_refresh_tokens SET revoked_at=CURRENT_TIMESTAMP, revoked_ip=@ip
            WHERE local_user_id=@id AND revoked_at IS NULL
            """);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("ip", ClientIp);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task CompleteResetRequests(Guid userId, string actor, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE public.local_password_reset_requests
            SET status='COMPLETED', resolved_at=CURRENT_TIMESTAMP, resolved_by=@actor
            WHERE local_user_id=@id AND status='PENDING'
            """);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("actor", actor);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task SendPasswordResetEmailSafely(string actorEmployeeId, LocalUser account,
        CancellationToken token)
    {
        try
        {
            var actor = await FindUserContact(actorEmployeeId, token);
            if (string.IsNullOrWhiteSpace(actor.Email) || string.IsNullOrWhiteSpace(account.Email)) return;
            var body = $"""
                <div style="font-family:Tahoma,Arial,sans-serif;color:#172442;line-height:1.7">
                  <h2>แจ้งข้อมูลบัญชี HR Portal</h2>
                  <p>ชื่อผู้ใช้: <strong>{System.Net.WebUtility.HtmlEncode(account.Username)}</strong></p>
                  <p>รหัสผ่านเริ่มต้น: <strong>{DefaultPassword}</strong></p>
                  <p>ระบบจะบังคับให้เปลี่ยนรหัสผ่านทันทีหลังเข้าสู่ระบบ</p>
                </div>
                """;
            await graphMailService.SendAsync(actor.Email, account.Email,
                "แจ้งรีเซ็ตรหัสผ่านบัญชี HR Portal", body, token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Local account password email failed for {EmployeeId}", account.EmployeeId);
        }
    }

    private async Task<(string Email, string Name)> FindUserContact(string employeeId, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT COALESCE(NULLIF(b.email_address,''),''),
                   COALESCE(NULLIF(b.full_name_th,''),NULLIF(b.full_name_en,''),e.employee_code)
            FROM public.employees e LEFT JOIN public.employee_basic_info b ON b.employee_id=e.id
            WHERE e.employee_code=@employee_id LIMIT 1
            """);
        command.Parameters.AddWithValue("employee_id", employeeId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? (reader.GetString(0), reader.GetString(1)) : (string.Empty, string.Empty);
    }

    private async Task WriteAudit(Guid? userId, string username, string? employeeId,
        bool success, string? reason, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO public.local_login_audit
                (local_user_id, username, employee_id, is_success, failure_reason, ip_address, user_agent)
            VALUES (@user_id, @username, @employee_id, @success, @reason, @ip, @user_agent)
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid?>("user_id", userId));
        command.Parameters.AddWithValue("username", username);
        command.Parameters.Add(new NpgsqlParameter<string?>("employee_id", employeeId));
        command.Parameters.AddWithValue("success", success);
        command.Parameters.Add(new NpgsqlParameter<string?>("reason", reason));
        command.Parameters.AddWithValue("ip", ClientIp);
        command.Parameters.AddWithValue("user_agent", Request.Headers.UserAgent.ToString()[..Math.Min(500, Request.Headers.UserAgent.ToString().Length)]);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<string?> ResolveActorEmployeeId(CancellationToken token)
    {
        var direct = User.FindFirst("employee_id")?.Value;
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        var tenantId = User.FindFirst("tid")?.Value;
        var objectId = User.FindFirst("oid")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId)) return null;
        await using var command = dataSource.CreateCommand("""
            SELECT employee_id FROM public.microsoft_accounts
            WHERE tenant_id=@tenant_id AND entra_object_id=@object_id AND is_active=TRUE
            LIMIT 1
            """);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("object_id", objectId);
        return (string?)await command.ExecuteScalarAsync(token);
    }

    private async Task<bool> HasManagementPermission(string actionKey, CancellationToken token)
    {
        var actor = await ResolveActorEmployeeId(token);
        return !string.IsNullOrWhiteSpace(actor) &&
               await pageAccessService.HasAccess(actor, "LOCAL_ACCOUNTS", token) &&
               await actionPermissionService.HasPermission(actor, "LOCAL_ACCOUNTS", actionKey, token);
    }

    private CookieOptions CookieOptions() => new()
    {
        HttpOnly = true,
        Secure = !environment.IsDevelopment(),
        SameSite = SameSiteMode.Lax,
        Expires = DateTimeOffset.UtcNow.AddHours(12),
        Path = "/api/local-auth"
    };

    private void DeleteRefreshCookie() => Response.Cookies.Delete(
        RefreshCookieName, new CookieOptions { Path = "/api/local-auth" });
    private string ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

    public sealed record LocalUser(
        Guid Id, string EmployeeId, string Username, string PasswordHash,
        bool IsActive, bool MustChangePassword, int FailedAccessCount,
        DateTimeOffset? LockoutEnd, bool EmployeeActive, string EmployeeStatus,
        string EmployeeName, string Email);
}
