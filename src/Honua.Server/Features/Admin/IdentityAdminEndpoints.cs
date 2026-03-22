// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for identity provider configuration and connectivity testing.
/// </summary>
internal static class IdentityAdminEndpoints
{
    internal sealed class IdentityAdminEndpointsLog;

    private const string IdentityTestHttpClient = "IdentityProviderTest";

    public static void MapIdentityAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/identity")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Identity")
            .RequireAdminAuthorization();

        group.MapGet("/providers", HandleGetProviders)
            .WithDisplayName("Get Identity Providers")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<IdentityProvidersResponse>>();

        group.MapGet("/providers/{providerType}/test", HandleTestProvider)
            .WithDisplayName("Test Identity Provider Connectivity")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<IdentityProviderTestResult>>();
    }

    private static IResult HandleGetProviders(
        [FromServices] IOptions<OidcAuthenticationOptions> oidcOptions,
        [FromServices] ILogger<IdentityAdminEndpointsLog> logger)
    {
        AdminLog.IdentityProvidersQueried(logger);

        var options = oidcOptions.Value;
        var providers = new List<IdentityProviderStatus>();

        if (options.AzureAd is { } azureAd)
        {
            providers.Add(new IdentityProviderStatus
            {
                Type = "AzureAd",
                Enabled = azureAd.Enabled,
                DisplayName = "Azure Active Directory",
                Authority = azureAd.IsValid ? $"{azureAd.Instance.TrimEnd('/')}/{azureAd.TenantId}/v2.0" : null,
                CallbackPath = azureAd.CallbackPath,
                Scopes = azureAd.Scopes,
                IsConfigurationValid = azureAd.IsValid
            });
        }

        if (options.Google is { } google)
        {
            providers.Add(new IdentityProviderStatus
            {
                Type = "Google",
                Enabled = google.Enabled,
                DisplayName = "Google",
                Authority = "https://accounts.google.com",
                CallbackPath = google.CallbackPath,
                Scopes = google.Scopes,
                IsConfigurationValid = google.IsValid
            });
        }

        if (options.Generic is { } generic)
        {
            providers.Add(new IdentityProviderStatus
            {
                Type = "Generic",
                Enabled = generic.Enabled,
                DisplayName = generic.DisplayName,
                Authority = generic.Authority,
                CallbackPath = generic.CallbackPath,
                Scopes = generic.Scopes,
                IsConfigurationValid = generic.IsValid
            });
        }

        var response = new IdentityProvidersResponse
        {
            Enabled = options.Enabled,
            Providers = providers.ToArray()
        };

        return Results.Json(
            ApiResponse<IdentityProvidersResponse>.CreateSuccess(response),
            IdentityAdminJsonContext.Default.ApiResponseIdentityProvidersResponse);
    }

    private static async Task<IResult> HandleTestProvider(
        string providerType,
        [FromServices] IOptions<OidcAuthenticationOptions> oidcOptions,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] ILogger<IdentityAdminEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        AdminLog.IdentityProviderTestStarted(logger, providerType);

        var authority = ResolveAuthority(oidcOptions.Value, providerType);
        if (authority is null)
        {
            var notFound = new IdentityProviderTestResult
            {
                ProviderType = providerType,
                IsReachable = false,
                ErrorMessage = $"Provider '{providerType}' is not configured or has no authority URL."
            };

            AdminLog.IdentityProviderTestCompleted(logger, providerType, false);

            return Results.Json(
                ApiResponse<IdentityProviderTestResult>.CreateSuccess(notFound),
                IdentityAdminJsonContext.Default.ApiResponseIdentityProviderTestResult);
        }

        var discoveryUrl = $"{authority.TrimEnd('/')}/.well-known/openid-configuration";

        try
        {
            using var client = httpClientFactory.CreateClient(IdentityTestHttpClient);
            client.Timeout = TimeSpan.FromSeconds(5);

            var stopwatch = Stopwatch.StartNew();
            using var response = await client.GetAsync(discoveryUrl, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            string? issuer = null;
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("issuer", out var issuerElement))
                    {
                        issuer = issuerElement.GetString();
                    }
                }
                catch (JsonException)
                {
                    // Issuer extraction is best-effort
                }
            }

            var isReachable = response.IsSuccessStatusCode;
            AdminLog.IdentityProviderTestCompleted(logger, providerType, isReachable);

            var result = new IdentityProviderTestResult
            {
                ProviderType = providerType,
                IsReachable = isReachable,
                ResponseTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                DiscoveryUrl = discoveryUrl,
                Issuer = issuer,
                ErrorMessage = isReachable ? null : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
            };

            return Results.Json(
                ApiResponse<IdentityProviderTestResult>.CreateSuccess(result),
                IdentityAdminJsonContext.Default.ApiResponseIdentityProviderTestResult);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            AdminLog.IdentityProviderTestCompleted(logger, providerType, false);

            var errorResult = new IdentityProviderTestResult
            {
                ProviderType = providerType,
                IsReachable = false,
                DiscoveryUrl = discoveryUrl,
                ErrorMessage = ex.Message
            };

            return Results.Json(
                ApiResponse<IdentityProviderTestResult>.CreateSuccess(errorResult),
                IdentityAdminJsonContext.Default.ApiResponseIdentityProviderTestResult);
        }
    }

    private static string? ResolveAuthority(OidcAuthenticationOptions options, string providerType) =>
        providerType.ToUpperInvariant() switch
        {
            "AZUREAD" => options.AzureAd is { Enabled: true, IsValid: true } azureAd
                ? $"{azureAd.Instance.TrimEnd('/')}/{azureAd.TenantId}/v2.0"
                : null,
            "GOOGLE" => options.Google is { Enabled: true, IsValid: true }
                ? "https://accounts.google.com"
                : null,
            "GENERIC" => options.Generic is { Enabled: true, IsValid: true } generic
                ? generic.Authority
                : null,
            _ => null
        };
}
