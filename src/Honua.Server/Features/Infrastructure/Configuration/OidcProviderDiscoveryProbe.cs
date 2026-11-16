// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var discoveryUrl = $"{provider.Authority.TrimEnd('/')}/.well-known/openid-configuration";
            var validation = await OutboundHttpUrlValidator.ValidateAsync(discoveryUrl, timeout.Token).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                return Unreachable("Authority URL must be a public HTTPS address.");
            }
            return await FetchAsync(provider, httpClientFactory, timeout.Token).ConfigureAwait(false);
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

    private static async Task<OidcProviderTestResult> FetchAsync(
        OidcProviderConfiguration provider, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(IdentityTestHttpClient);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            provider.Authority.TrimEnd('/') + "/.well-known/openid-configuration");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(65536, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false),
            new JsonDocumentOptions { MaxDepth = 16 });
        var matches = document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("issuer", out var issuer)
            && issuer.ValueKind == JsonValueKind.String
            && IsExpectedIssuer(provider.Authority, provider.ProviderType, issuer.GetString());
        return matches ? new OidcProviderTestResult
        {
            IsReachable = true,
            Message = "Provider discovery document was retrieved and its issuer matched.",
            TestedAt = DateTimeOffset.UtcNow,
        } : Unreachable("Provider discovery document has no matching issuer.");
    }

    internal static bool IsExpectedIssuer(string? configuredAuthority, string providerType, string? issuer)
    {
        var authority = configuredAuthority?.TrimEnd('/');
        if (string.IsNullOrEmpty(authority) || string.IsNullOrEmpty(issuer))
        {
            return false;
        }
        if (string.Equals(issuer.TrimEnd('/'), authority, StringComparison.Ordinal))
        {
            return true;
        }

        // Entra's tenant-independent discovery returns a template, not the configured alias.
        // This only checks discovery health; token issuer validation remains with the OIDC handler.
        if (providerType != "AzureAd" || !Uri.TryCreate(authority, UriKind.Absolute, out var uri))
        {
            return false;
        }
        var tenant = uri.AbsolutePath.Trim('/').Split('/');
        if (tenant.Length != 2 || tenant[1] != "v2.0")
        {
            return false;
        }
        var expectedTenant = tenant[0] switch
        {
            "common" or "organizations" => "{tenantid}",
            "consumers" => "9188040d-6c67-4c5b-b112-36a304b66dad",
            _ => null
        };
        return expectedTenant is not null && string.Equals(issuer.TrimEnd('/'),
            uri.GetLeftPart(UriPartial.Authority) + "/" + expectedTenant + "/v2.0", StringComparison.Ordinal);
    }

    private static OidcProviderTestResult Unreachable(string message) => new()
    {
        IsReachable = false,
        Message = message,
        TestedAt = DateTimeOffset.UtcNow,
    };
}
