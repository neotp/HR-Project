using System.Net.Http.Json;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace HrProject.Client.Services;

public sealed class PageAvailabilityState : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly AuthenticationStateProvider authenticationStateProvider;
    private static readonly HashSet<string> PublicPageKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "LEAVE_DOCUMENTS", "LEAVE_ALL_DOCUMENTS", "ATTENDANCE",
        "ATTENDANCE_RECORDS", "EMPLOYEES"
    };
    private readonly SemaphoreSlim loadLock = new(1, 1);
    private IReadOnlyList<ApplicationPageAvailabilityDto> pages = [];
    private readonly Dictionary<string, CurrentPageAccessDto> currentAccess =
        new(StringComparer.OrdinalIgnoreCase);
    private string? loadedIdentity;

    public PageAvailabilityState(
        HttpClient httpClient,
        AuthenticationStateProvider authenticationStateProvider)
    {
        this.httpClient = httpClient;
        this.authenticationStateProvider = authenticationStateProvider;
        authenticationStateProvider.AuthenticationStateChanged += HandleAuthenticationStateChanged;
    }

    public event Action? Changed;

    public bool IsLoaded { get; private set; }
    public IReadOnlyList<ApplicationPageAvailabilityDto> Pages => pages;

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        var identity = await GetIdentityKeyAsync();
        if (IsLoaded && string.Equals(loadedIdentity, identity, StringComparison.Ordinal))
            return;

        var changed = false;
        await loadLock.WaitAsync(cancellationToken);
        try
        {
            // MainLayout and NavMenu initialize together. Recheck after taking the
            // lock so the second caller does not repeat the same permission load.
            identity = await GetIdentityKeyAsync();
            if (IsLoaded && string.Equals(loadedIdentity, identity, StringComparison.Ordinal)) return;
            await LoadAsync(cancellationToken);
            loadedIdentity = identity;
            changed = true;
        }
        finally
        {
            loadLock.Release();
        }

        if (changed) Changed?.Invoke();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var identity = await GetIdentityKeyAsync();
        await loadLock.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            loadedIdentity = identity;
        }
        finally
        {
            loadLock.Release();
        }

        Changed?.Invoke();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        pages = await httpClient.GetFromJsonAsync<List<ApplicationPageAvailabilityDto>>(
            "api/page-permissions/availability", cancellationToken) ?? [];
        currentAccess.Clear();
        try
        {
            IReadOnlyList<CurrentPageAccessDto> accessResults;
            try
            {
                accessResults = await httpClient.GetFromJsonAsync<List<CurrentPageAccessDto>>(
                    "api/page-permissions/current-access", cancellationToken) ?? [];
            }
            catch (HttpRequestException)
            {
                // Rolling-deployment fallback for an older API instance.
                var accessTasks = pages.Select(page => page.PageKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(pageKey => httpClient.GetFromJsonAsync<CurrentPageAccessDto>(
                        $"api/page-permissions/current-access/{pageKey}", cancellationToken))
                    .ToArray();
                accessResults = (await Task.WhenAll(accessTasks))
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .ToList();
            }
            foreach (var pageAccess in accessResults)
                currentAccess[pageAccess.PageKey] = pageAccess;
        }
        catch
        {
            foreach (var page in pages)
            {
                var allowed = PublicPageKeys.Contains(page.PageKey);
                currentAccess[page.PageKey] = new CurrentPageAccessDto(
                    page.PageKey, allowed, allowed, false);
            }
        }
        IsLoaded = true;
    }

    public bool IsEnabled(string pageKey) =>
        !IsLoaded || pages.FirstOrDefault(page =>
            string.Equals(page.PageKey, pageKey, StringComparison.OrdinalIgnoreCase))?.IsEnabled != false;

    public bool HasAccess(string pageKey) =>
        !IsLoaded ? PublicPageKeys.Contains(pageKey) :
        currentAccess.TryGetValue(pageKey, out var access) && access.CanAccess;

    public void UseOpenFallback()
    {
        pages = [];
        currentAccess.Clear();
        IsLoaded = true;
        loadedIdentity = null;
        Changed?.Invoke();
    }

    private async void HandleAuthenticationStateChanged(Task<AuthenticationState> stateTask)
    {
        try
        {
            await stateTask;
            // Let the token provider/local session finish publishing the new token
            // before requesting the permission endpoints.
            await Task.Yield();
            await RefreshAsync();
        }
        catch
        {
            // MainLayout/NavMenu will retry through EnsureLoadedAsync on navigation.
            loadedIdentity = null;
        }
    }

    private async Task<string> GetIdentityKeyAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        var user = state.User;
        if (user.Identity?.IsAuthenticated != true) return "anonymous";
        return user.FindFirst("employee_id")?.Value
            ?? user.FindFirst("oid")?.Value
            ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? user.Identity.Name
            ?? "authenticated";
    }

    public ApplicationPageAvailabilityDto? FindClosedPage(string absoluteUri)
    {
        if (!IsLoaded)
            return null;

        var path = new Uri(absoluteUri).AbsolutePath;
        path = NormalizePath(path);

        return pages.FirstOrDefault(page =>
            !page.IsEnabled && RouteMatches(NormalizePath(page.RoutePath), path));
    }

    public ApplicationPageAvailabilityDto? FindDeniedPage(string absoluteUri)
    {
        if (!IsLoaded)
            return null;

        var path = NormalizePath(new Uri(absoluteUri).AbsolutePath);
        return pages.FirstOrDefault(page =>
            page.IsEnabled && !HasAccess(page.PageKey) &&
            RouteMatches(NormalizePath(page.RoutePath), path));
    }

    private static bool RouteMatches(string configuredRoute, string currentPath)
    {
        if (configuredRoute == "/")
            return currentPath == "/";

        return string.Equals(configuredRoute, currentPath, StringComparison.OrdinalIgnoreCase) ||
               currentPath.StartsWith(configuredRoute + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        var normalized = "/" + path.Trim().Trim('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

    public void Dispose() =>
        authenticationStateProvider.AuthenticationStateChanged -= HandleAuthenticationStateChanged;
}
