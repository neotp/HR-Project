using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace HrProject.Client.Services;

public sealed class LocalOrMicrosoftAuthorizationMessageHandler(
    LocalAuthSession localSession,
    IAccessTokenProvider microsoftTokenProvider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var localToken = await localSession.GetAccessTokenAsync(cancellationToken);
        HttpRequestMessage? retryRequest = null;
        if (!string.IsNullOrWhiteSpace(localToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", localToken);
            retryRequest = await CloneAsync(request, cancellationToken);
        }
        else
        {
            var result = await microsoftTokenProvider.RequestAccessToken();
            if (result.TryGetToken(out var token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        }
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized ||
            string.IsNullOrWhiteSpace(localToken) || retryRequest is null)
            return response;

        response.Dispose();
        localSession.InvalidateAccessToken(localToken);
        var refreshedToken = await localSession.GetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(refreshedToken))
        {
            retryRequest.Dispose();
            return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                RequestMessage = request,
                ReasonPhrase = "Local session expired"
            };
        }

        retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken);
        return await base.SendAsync(retryRequest, cancellationToken);
    }

    private static async Task<HttpRequestMessage> CloneAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }
}
