// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Admin;
using Honua.Admin.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Options;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();
builder.Services.AddAuthorizationCore();
builder.Services.AddOidcAuthentication(options =>
{
    builder.Configuration.Bind("Oidc", options.ProviderOptions);
});

builder.Services.Configure<AdminApiOptions>(builder.Configuration.GetSection(AdminApiOptions.SectionName));

builder.Services.AddHttpClient("AdminApi", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<AdminApiOptions>>().Value;
    var baseUrl = AdminApiUrlResolver.Resolve(options.BaseUrl, builder.HostEnvironment.BaseAddress);
    client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
}).AddHttpMessageHandler(sp =>
{
    var options = sp.GetRequiredService<IOptions<AdminApiOptions>>().Value;
    var baseUrl = AdminApiUrlResolver.Resolve(options.BaseUrl, builder.HostEnvironment.BaseAddress);
    var scopes = options.Scopes.Length == 0 ? ["honua.admin"] : options.Scopes;

    return sp.GetRequiredService<AuthorizationMessageHandler>()
        .ConfigureHandler([baseUrl], scopes);
});

builder.Services.AddScoped(sp =>
    new HonuaApiClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("AdminApi")));
builder.Services.AddScoped<ISecureConnectionsClient, SecureConnectionsClient>();
builder.Services.AddScoped<ILayerPublishingClient, LayerPublishingClient>();

await builder.Build().RunAsync();
