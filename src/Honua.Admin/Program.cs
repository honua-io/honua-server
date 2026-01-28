// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Admin;
using Honua.Admin.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Options;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();
builder.Services.Configure<AdminAuthorizationOptions>(
    builder.Configuration.GetSection(AdminAuthorizationOptions.SectionName));

var authorizationOptions = builder.Configuration
    .GetSection(AdminAuthorizationOptions.SectionName)
    .Get<AdminAuthorizationOptions>() ?? new AdminAuthorizationOptions();

var oidcSection = builder.Configuration.GetSection("Oidc");
var oidcAuthority = oidcSection["Authority"];
var oidcClientId = oidcSection["ClientId"];
var oidcEnabled = IsOidcConfigured(oidcAuthority, oidcClientId);

builder.Services.AddAuthorizationCore(options =>
{
    options.AddPolicy(AdminAuthorizationPolicies.AdminPolicy, policy =>
    {
        policy.RequireAuthenticatedUser();
        if (authorizationOptions.AdminRoles.Length > 0)
        {
            policy.RequireRole(authorizationOptions.AdminRoles);
        }
    });
});

if (oidcEnabled)
{
    builder.Services.AddOidcAuthentication(options =>
    {
        builder.Configuration.Bind("Oidc", options.ProviderOptions);
        if (!string.IsNullOrWhiteSpace(authorizationOptions.RoleClaimType))
        {
            options.UserOptions.RoleClaim = authorizationOptions.RoleClaimType;
        }
    });
}
else
{
    builder.Services.AddScoped<AuthenticationStateProvider, AnonymousAuthenticationStateProvider>();
}

builder.Services.Configure<AdminApiOptions>(builder.Configuration.GetSection(AdminApiOptions.SectionName));

var adminApiClientBuilder = builder.Services.AddHttpClient("AdminApi", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<AdminApiOptions>>().Value;
    var baseUrl = AdminApiUrlResolver.Resolve(options.BaseUrl, builder.HostEnvironment.BaseAddress);
    client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
});

if (oidcEnabled)
{
    adminApiClientBuilder.AddHttpMessageHandler(sp =>
    {
        var options = sp.GetRequiredService<IOptions<AdminApiOptions>>().Value;
        var baseUrl = AdminApiUrlResolver.Resolve(options.BaseUrl, builder.HostEnvironment.BaseAddress);
        var scopes = options.Scopes.Length == 0 ? ["honua.admin"] : options.Scopes;
        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        var metricsUri = new Uri(baseUri, "/api/v1/metrics/");
        var healthUri = new Uri(baseUri, "/healthz/");
        var tilesUri = new Uri(baseUri, "/tiles/");
        var authorizedUrls = new[]
        {
            baseUri.ToString(),
            metricsUri.ToString(),
            healthUri.ToString(),
            tilesUri.ToString()
        };

        return sp.GetRequiredService<AuthorizationMessageHandler>()
            .ConfigureHandler(authorizedUrls, scopes);
    });
}

builder.Services.AddScoped(sp =>
    new HonuaApiClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("AdminApi")));
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<ISecureConnectionsClient, SecureConnectionsClient>();
builder.Services.AddScoped<ILayerPublishingClient, LayerPublishingClient>();
builder.Services.AddScoped<IEsriImportClient, EsriImportClient>();
builder.Services.AddScoped<IFileImportClient, FileImportClient>();
builder.Services.AddScoped<ILayerStyleClient, LayerStyleClient>();

await builder.Build().RunAsync();

static bool IsOidcConfigured(string? authority, string? clientId)
{
    if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(clientId))
    {
        return false;
    }

    var normalizedAuthority = authority.Trim().TrimEnd('/');
    return !normalizedAuthority.Equals("https://identity.example.com", StringComparison.OrdinalIgnoreCase);
}
