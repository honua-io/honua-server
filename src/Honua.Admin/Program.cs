// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Admin;
using Honua.Admin.Features.Auth.Services;
using Honua.Admin.Features.GitOps.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Register HttpClient with the server base address and basic resilience.
// When hosted integrated, the base address matches the server origin.
// Note: WebAssembly clients have limited resilience options compared to server-side clients
builder.Services.AddScoped(sp =>
{
    var httpClient = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
    // Set reasonable timeout for admin operations
    httpClient.Timeout = TimeSpan.FromSeconds(30);
    return httpClient;
});

// Register auth services
builder.Services.AddScoped<AuthStateStore>();
builder.Services.AddScoped<AuthBootstrapService>();
builder.Services.AddScoped<OidcSessionService>();
builder.Services.AddScoped<AdminAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<AdminAuthStateProvider>());
builder.Services.AddScoped<GitOpsAdminClient>();

builder.Services.AddAuthorizationCore();

await builder.Build().RunAsync().ConfigureAwait(false);
