// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// In-memory OIDC provider store for the admin API surface.
/// Records live for the process and are published into the authentication catalog.
/// A durable store is tracked separately (#496).
/// </summary>
internal sealed class InMemoryOidcProviderStore : IOidcProviderStore
{
    private const string IdentityTestHttpClient = "IdentityProviderTest";

    private readonly ConcurrentDictionary<Guid, OidcProviderConfiguration> _providers = new();
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly OidcRuntimeProviderRegistry? _registry;

    /// <summary>
    /// Initializes a new instance. Both dependencies are optional so hosts and tests that
    /// construct the store directly keep compiling; without a factory the connectivity test
    /// reports that it could not probe.
    /// </summary>
    public InMemoryOidcProviderStore(
        IHttpClientFactory? httpClientFactory = null,
        OidcRuntimeProviderRegistry? registry = null)
    {
        _httpClientFactory = httpClientFactory;
        _registry = registry;
    }

    public Task<IReadOnlyList<OidcProviderConfiguration>> ListProvidersAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OidcProviderConfiguration> result = _providers.Values.ToList().AsReadOnly();
        return Task.FromResult(result);
    }

    public Task<OidcProviderConfiguration?> GetProviderAsync(Guid providerId, CancellationToken cancellationToken = default)
    {
        _providers.TryGetValue(providerId, out var provider);
        return Task.FromResult(provider);
    }

    public Task<OidcProviderConfiguration> CreateProviderAsync(OidcProviderConfiguration provider, CancellationToken cancellationToken = default)
    {
        if (!_providers.TryAdd(provider.ProviderId, provider))
        {
            throw new InvalidOperationException($"Provider with ID '{provider.ProviderId}' already exists.");
        }

        Publish();
        return Task.FromResult(provider);
    }

    public Task<OidcProviderConfiguration?> UpdateProviderAsync(OidcProviderConfiguration provider, CancellationToken cancellationToken = default)
    {
        if (!_providers.ContainsKey(provider.ProviderId))
        {
            return Task.FromResult<OidcProviderConfiguration?>(null);
        }

        _providers[provider.ProviderId] = provider;
        Publish();
        return Task.FromResult<OidcProviderConfiguration?>(provider);
    }

    public Task<bool> DeleteProviderAsync(Guid providerId, CancellationToken cancellationToken = default)
    {
        var removed = _providers.TryRemove(providerId, out _);
        if (removed)
        {
            Publish();
        }

        return Task.FromResult(removed);
    }

    public async Task<OidcProviderTestResult> TestProviderAsync(Guid providerId, CancellationToken cancellationToken = default)
    {
        if (!_providers.TryGetValue(providerId, out var provider))
        {
            return Unreachable("Provider not found");
        }

        if (!provider.Enabled)
        {
            return Unreachable("Provider is disabled");
        }

        if (_httpClientFactory is null)
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

        var discoveryUrl = $"{provider.Authority.TrimEnd('/')}/.well-known/openid-configuration";
        try
        {
            using var client = _httpClientFactory.CreateClient(IdentityTestHttpClient);
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

    private void Publish()
    {
        if (_registry is null)
        {
            return;
        }

        var published = _providers.Values
            .Select(provider => new OidcRuntimeProvider(
                provider.ProviderId.ToString("D"),
                provider.ProviderType,
                provider.Name,
                provider.Authority,
                provider.ClientId,
                provider.Enabled))
            .ToArray();
        _registry.Replace(published);
    }

    private static OidcProviderTestResult Unreachable(string message) => new()
    {
        IsReachable = false,
        Message = message,
        TestedAt = DateTimeOffset.UtcNow,
    };
}
