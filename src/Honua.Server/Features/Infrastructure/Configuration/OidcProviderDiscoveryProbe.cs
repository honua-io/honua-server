// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Identity.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal static class OidcProviderDiscoveryProbe
{
    private const string IdentityTestHttpClient = "IdentityProviderTest";

    public static async Task<OidcProviderTestResult> TestAsync(
        OidcProviderConfiguration? provider, IHttpClientFactory? httpClientFactory, CancellationToken cancellationToken)
    {
        if (provider is null) { return Unreachable("Provider not found"); }
        if (!provider.Enabled)
        {
            return Unreachable("Provider is disabled");
        }

        if (httpClientFactory is null)
        {
            return Unreachable("Discovery probe is not configured.");
        }

        var ssrfCheck = await OutboundHttpUrlValidator
            .ValidateAsync(provider.Authority, cancellationToken)
            .ConfigureAwait(false);
        if (!ssrfCheck.IsValid)
        {
            return Unreachable($"Authority URL validation failed: {ssrfCheck.ErrorMessage}");
        }

        return await FetchAsync(provider.Authority, httpClientFactory, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OidcProviderTestResult> FetchAsync(
        string authority, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken)
    {
        var discoveryUrl = $"{authority.TrimEnd('/')}/.well-known/openid-configuration";
        try
        {
            using var client = httpClientFactory.CreateClient(IdentityTestHttpClient);
            client.Timeout = TimeSpan.FromSeconds(5);
            using var response = await client.GetAsync(discoveryUrl, cancellationToken).ConfigureAwait(false);
            return new OidcProviderTestResult
            {
                IsReachable = response.IsSuccessStatusCode,
                Message = response.IsSuccessStatusCode
                    ? "Provider discovery document was retrieved."
                    : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                TestedAt = DateTimeOffset.UtcNow,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Unreachable("Identity provider discovery request failed.");
        }
    }

    private static OidcProviderTestResult Unreachable(string message) => new()
    {
        IsReachable = false,
        Message = message,
        TestedAt = DateTimeOffset.UtcNow,
    };
}
