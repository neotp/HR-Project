using System.Net.Http.Json;
using HrProject.Shared.Models;

namespace HrProject.Client.Services;

public sealed class PageAvailabilityState(HttpClient httpClient)
{
    private static readonly HashSet<string> PublicPageKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "LEAVE_DOCUMENTS", "LEAVE_ALL_DOCUMENTS", "ATTENDANCE",
        "ATTENDANCE_RECORDS", "EMPLOYEES"
    };
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
            currentAccess.Clear();
            try
            {
                var accessTasks = pages.Select(page => page.PageKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(pageKey => httpClient.GetFromJsonAsync<CurrentPageAccessDto>(
                        $"api/page-permissions/current-access/{pageKey}", cancellationToken))
                    .ToArray();
                var accessResults = await Task.WhenAll(accessTasks);
                foreach (var pageAccess in accessResults)
                {
                    if (pageAccess is not null)
                        currentAccess[pageAccess.PageKey] = pageAccess;
                }
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
        !IsLoaded ? PublicPageKeys.Contains(pageKey) :
        currentAccess.TryGetValue(pageKey, out var access) && access.CanAccess;

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

        return string.Equals(configuredRoute, currentPath, StringComparison.OrdinalIgnoreCase) ||
               currentPath.StartsWith(configuredRoute + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        var normalized = "/" + path.Trim().Trim('/');
        return normalized.Length == 0 ? "/" : normalized;
    }
}
