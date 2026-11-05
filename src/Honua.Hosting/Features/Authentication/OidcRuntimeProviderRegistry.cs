// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

internal sealed record OidcRuntimeProvider(
    string Key,
    string ProviderType,
    string DisplayName,
    string Authority,
    string ClientId,
    bool Enabled,
    string? ClientSecret = null);

internal interface IOidcRuntimeProviderSource
{
    IReadOnlyList<OidcRuntimeProvider> Current { get; }
}

internal sealed class OidcRuntimeProviderRegistry : IOidcRuntimeProviderSource
{
    private readonly object _gate = new();
    private IReadOnlyList<OidcRuntimeProvider> _current = [];

    public OidcRuntimeProviderRegistry()
    {
        OidcProviderCatalog.Attach(this);
    }

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

    public void Replace(IReadOnlyList<OidcRuntimeProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        lock (_gate)
        {
            _current = providers;
        }
    }
}
