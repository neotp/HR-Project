using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Authentication.WebAssembly.Msal.Models;

namespace HrProject.Client.Services;

public sealed class CompositeAuthenticationStateProvider : AuthenticationStateProvider, IDisposable
{
    private readonly LocalAuthSession localSession;
    private readonly AuthenticationStateProvider microsoftProvider;

    public CompositeAuthenticationStateProvider(
        LocalAuthSession localSession,
        RemoteAuthenticationService<RemoteAuthenticationState, RemoteUserAccount, MsalProviderOptions> microsoftAuthentication)
    {
        this.localSession = localSession;
        microsoftProvider = microsoftAuthentication;
        localSession.StateChanged += OnLocalStateChanged;
        microsoftProvider.AuthenticationStateChanged += OnMicrosoftStateChanged;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (await localSession.TryRestoreAsync())
            return new AuthenticationState(localSession.CreatePrincipal());
        return await microsoftProvider.GetAuthenticationStateAsync();
    }

    public void NotifyLocalStateChanged() => OnLocalStateChanged();

    private void OnLocalStateChanged() => NotifyAuthenticationStateChanged(
        Task.FromResult(new AuthenticationState(localSession.CreatePrincipal())));

    private void OnMicrosoftStateChanged(Task<AuthenticationState> state) =>
        NotifyAuthenticationStateChanged(state);

    public void Dispose()
    {
        localSession.StateChanged -= OnLocalStateChanged;
        microsoftProvider.AuthenticationStateChanged -= OnMicrosoftStateChanged;
    }
}
