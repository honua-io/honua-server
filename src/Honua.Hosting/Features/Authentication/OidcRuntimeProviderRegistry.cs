// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// An OIDC provider registered at runtime (the admin provider API) rather than in options.
/// Records live for the process; a durable store is tracked separately.
/// </summary>
internal sealed record OidcRuntimeProvider(
    string Key,
    string ProviderType,
    string DisplayName,
    string Authority,
    string ClientId,
    bool Enabled);

/// <summary>
/// Process-lifetime view of admin-registered OIDC providers.
/// </summary>
internal interface IOidcRuntimeProviderSource
{
    /// <summary>Providers currently registered. Empty when none have been published.</summary>
    IReadOnlyList<OidcRuntimeProvider> Current { get; }
}

/// <summary>
/// Singleton registry the in-memory provider store publishes into and authentication reads.
/// </summary>
internal sealed class OidcRuntimeProviderRegistry : IOidcRuntimeProviderSource
{
    private readonly object _gate = new();
    private IReadOnlyList<OidcRuntimeProvider> _current = [];

    /// <summary>
    /// Initializes a new instance and attaches it as the process catalog source so
    /// options-only catalog callers see admin-registered providers.
    /// </summary>
    public OidcRuntimeProviderRegistry()
    {
        OidcProviderCatalog.Attach(this);
    }

    /// <inheritdoc />
    public IReadOnlyList<OidcRuntimeProvider> Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Replaces the published set. Called after every store mutation.</summary>
    public void Replace(IReadOnlyList<OidcRuntimeProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        lock (_gate)
        {
            _current = providers;
        }
    }
}
