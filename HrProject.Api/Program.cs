using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
var connectionString = builder.Configuration.GetConnectionString("HrDatabase")
    ?? throw new InvalidOperationException("Connection string 'HrDatabase' is not configured.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
var tenantId = builder.Configuration["AzureAd:TenantId"]
    ?? throw new InvalidOperationException("AzureAd:TenantId is not configured.");
var clientId = builder.Configuration["AzureAd:ClientId"]
    ?? throw new InvalidOperationException("AzureAd:ClientId is not configured.");
var requiredScope = builder.Configuration["AzureAd:Scope"] ?? "users.read";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidAudiences =
            [
                $"api://{clientId}",
                clientId
            ],
            ValidIssuers =
            [
                $"https://login.microsoftonline.com/{tenantId}/v2.0",
                $"https://sts.windows.net/{tenantId}/"
            ],
            NameClaimType = "name"
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("HrApiScope", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            var scopes = context.User.FindFirst("scp")?.Value?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            return scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase) ||
                   context.User.HasClaim("roles", "Users.Read");
        });
    });
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context =>
        {
            var scopes = context.User.FindFirst("scp")?.Value?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            return scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase) ||
                   context.User.HasClaim("roles", "Users.Read");
        })
        .Build();
});
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("https://localhost:7169", "http://localhost:5043")
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/api/health/database", async (NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
{
    await using var command = dataSource.CreateCommand("SELECT current_database(), current_schema(), now()");
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    await reader.ReadAsync(cancellationToken);
    return Results.Ok(new
    {
        Database = reader.GetString(0),
        Schema = reader.GetString(1),
        ServerTime = reader.GetDateTime(2)
    });
}).AllowAnonymous();

app.Run();
