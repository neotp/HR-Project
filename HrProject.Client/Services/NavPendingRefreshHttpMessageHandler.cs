namespace HrProject.Client.Services;

public sealed class NavPendingRefreshHttpMessageHandler(
    NavPendingRefreshState refreshState) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (request.Method != HttpMethod.Get && response.IsSuccessStatusCode)
            refreshState.RequestRefresh();

        return response;
    }
}
