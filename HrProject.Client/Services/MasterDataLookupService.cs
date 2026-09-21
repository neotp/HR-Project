using HrProject.Shared.Models;
using System.Net.Http.Json;

namespace HrProject.Client.Services;

public sealed class MasterDataLookupService(IHttpClientFactory httpClientFactory)
{
    private readonly Dictionary<string, IReadOnlyList<MasterDataItemDto>> categoryCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<MasterDataItemDto>> childCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim cacheLock = new(1, 1);

    public async Task<IReadOnlyList<MasterDataItemDto>> GetCategoriesAsync(
        IEnumerable<string> categoryCodes,
        CancellationToken cancellationToken = default)
    {
        var requested = categoryCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await cacheLock.WaitAsync(cancellationToken);
        try
        {
            var missing = requested.Where(code => !categoryCache.ContainsKey(code)).ToArray();
            if (missing.Length > 0)
            {
                var client = httpClientFactory.CreateClient("HrApi");
                var query = Uri.EscapeDataString(string.Join(',', missing));
                var result = await client.GetFromJsonAsync<List<MasterDataItemDto>>(
                    $"api/system-master-data/items/all?categories={query}", cancellationToken) ?? [];

                foreach (var category in missing)
                {
                    categoryCache[category] = result
                        .Where(item => string.Equals(item.CategoryCode, category, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
            }

            return requested.SelectMany(code => categoryCache[code]).ToList();
        }
        finally
        {
            cacheLock.Release();
        }
    }

    public async Task<IReadOnlyList<MasterDataItemDto>> GetChildrenAsync(
        string categoryCode,
        long parentItemId,
        CancellationToken cancellationToken = default)
    {
        var category = categoryCode.Trim().ToUpperInvariant();
        var key = $"{category}:{parentItemId}";

        await cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (childCache.TryGetValue(key, out var cached))
                return cached;

            var client = httpClientFactory.CreateClient("HrApiBackground");
            var result = await client.GetFromJsonAsync<List<MasterDataItemDto>>(
                $"api/system-master-data/items?category={Uri.EscapeDataString(category)}&includeInactive=false&parentItemId={parentItemId}",
                cancellationToken) ?? [];
            childCache[key] = result;
            return result;
        }
        finally
        {
            cacheLock.Release();
        }
    }
}
