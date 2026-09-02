using System.Net.Http.Json;
using HrProject.Shared.Models;

namespace HrProject.Client.Services;

public sealed class PageAvailabilityState(HttpClient httpClient)
{
    private readonly SemaphoreSlim loadLock = new(1, 1);
    private IReadOnlyList<ApplicationPageAvailabilityDto> pages = [];
    private readonly Dictionary<string, CurrentPageAccessDto> currentAccess =
        new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public bool IsLoaded { get; private set; }
    public IReadOnlyList<ApplicationPageAvailabilityDto> Pages => pages;

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoaded)
            return;

        await RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await loadLock.WaitAsync(cancellationToken);
        try
        {
            pages = await httpClient.GetFromJsonAsync<List<ApplicationPageAvailabilityDto>>(
                "api/page-permissions/availability", cancellationToken) ?? [];
            try
            {
                foreach (var pageKey in new[] { "LEAVE_PENDING", "LEAVE_QUOTA_MOVEMENTS", "ATTENDANCE_REVIEWS" })
                {
                    var pageAccess = await httpClient.GetFromJsonAsync<CurrentPageAccessDto>(
                        $"api/page-permissions/current-access/{pageKey}", cancellationToken);
                    if (pageAccess is not null)
                        currentAccess[pageAccess.PageKey] = pageAccess;
                }
            }
            catch
            {
                currentAccess["LEAVE_PENDING"] = new CurrentPageAccessDto(
                    "LEAVE_PENDING", false, false, false);
                currentAccess["LEAVE_QUOTA_MOVEMENTS"] = new CurrentPageAccessDto(
                    "LEAVE_QUOTA_MOVEMENTS", false, false, false);
                currentAccess["ATTENDANCE_REVIEWS"] = new CurrentPageAccessDto(
                    "ATTENDANCE_REVIEWS", false, false, false);
            }
            IsLoaded = true;
        }
        finally
        {
            loadLock.Release();
        }

        Changed?.Invoke();
    }

    public bool IsEnabled(string pageKey) =>
        !IsLoaded || pages.FirstOrDefault(page =>
            string.Equals(page.PageKey, pageKey, StringComparison.OrdinalIgnoreCase))?.IsEnabled != false;

    public bool HasAccess(string pageKey) =>
        !IsLoaded || !currentAccess.TryGetValue(pageKey, out var access) || access.CanAccess;

    public void UseOpenFallback()
    {
        pages = [];
        currentAccess.Clear();
        IsLoaded = true;
        Changed?.Invoke();
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

        return string.Equals(configuredRoute, currentPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        var normalized = "/" + path.Trim().Trim('/');
        return normalized.Length == 0 ? "/" : normalized;
    }
}
