// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// In-memory OIDC provider store for the admin API surface.
/// Will be replaced by a persistent implementation when #496 lands.
/// </summary>
internal sealed class InMemoryOidcProviderStore : IOidcProviderStore
{
    private readonly ConcurrentDictionary<Guid, OidcProviderConfiguration> _providers = new();

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

        return Task.FromResult(provider);
    }

    public Task<OidcProviderConfiguration?> UpdateProviderAsync(OidcProviderConfiguration provider, CancellationToken cancellationToken = default)
    {
        if (!_providers.ContainsKey(provider.ProviderId))
        {
            return Task.FromResult<OidcProviderConfiguration?>(null);
        }

        _providers[provider.ProviderId] = provider;
        return Task.FromResult<OidcProviderConfiguration?>(provider);
    }

    public Task<bool> DeleteProviderAsync(Guid providerId, CancellationToken cancellationToken = default)
        => Task.FromResult(_providers.TryRemove(providerId, out _));

    public Task<OidcProviderTestResult> TestProviderAsync(Guid providerId, CancellationToken cancellationToken = default)
    {
        var exists = _providers.TryGetValue(providerId, out var provider);
        var result = new OidcProviderTestResult
        {
            IsReachable = exists && (provider?.Enabled ?? false),
            Message = exists
                ? provider!.Enabled ? "Provider is reachable" : "Provider is disabled"
                : "Provider not found",
            TestedAt = DateTimeOffset.UtcNow,
        };

        return Task.FromResult(result);
    }
}
