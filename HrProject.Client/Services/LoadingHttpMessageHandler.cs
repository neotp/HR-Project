namespace HrProject.Client.Services;

public sealed class LoadingHttpMessageHandler(LoadingState loadingState) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || IsAttachmentRequest(request.RequestUri))
            return await base.SendAsync(request, cancellationToken);

        using var loading = loadingState.BeginRequest();
        return await base.SendAsync(request, cancellationToken);
    }

    private static bool IsAttachmentRequest(Uri? uri) =>
        uri?.AbsolutePath.Contains("/attachments", StringComparison.OrdinalIgnoreCase) == true;
}
