using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using HrProject.Client;
using HrProject.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiOrigin = builder.Configuration["Api:BaseUrl"] ?? "http://localhost:5025";
var apiScope = builder.Configuration["Api:Scope"]
    ?? "api://da6648e6-17b3-408b-8fc0-bf570f846ae7/users.read";

// HttpClientFactory creates message handlers in its own DI scope. LoadingState
// must be shared with MainLayout so request activity reaches the visible UI.
builder.Services.AddSingleton<LoadingState>();
builder.Services.AddTransient<LoadingHttpMessageHandler>();

builder.Services.AddHttpClient("HrApi", client =>
{
    client.BaseAddress = new Uri($"{apiOrigin.TrimEnd('/')}/");
})
.AddHttpMessageHandler(provider =>
    provider.GetRequiredService<AuthorizationMessageHandler>()
        .ConfigureHandler(
            authorizedUrls: [apiOrigin.TrimEnd('/')],
            scopes: [apiScope]))
.AddHttpMessageHandler<LoadingHttpMessageHandler>();

builder.Services.AddScoped(provider =>
    provider.GetRequiredService<IHttpClientFactory>().CreateClient("HrApi"));
builder.Services.AddScoped<PageAvailabilityState>();

builder.Services.AddMsalAuthentication(options =>
{
    builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
    options.ProviderOptions.LoginMode = "redirect";
    options.ProviderOptions.DefaultAccessTokenScopes.Add(apiScope);
    options.ProviderOptions.Authentication.PostLogoutRedirectUri =
        builder.HostEnvironment.BaseAddress.TrimEnd('/') +
        "/authentication/logout-callback";
});

await builder.Build().RunAsync();
