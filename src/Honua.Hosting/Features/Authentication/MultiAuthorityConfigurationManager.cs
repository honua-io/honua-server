// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Infrastructure.Authentication;

// Signature validation uses only the keys for the token issuer; a merged key union would let one provider sign a token that names another issuer.
internal sealed class MultiAuthorityConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
{
    private readonly (string Authority, ConfigurationManager<OpenIdConnectConfiguration> Manager)[] _inner;
    private readonly (string Authority, OpenIdConnectConfiguration Configuration)[]? _seeded;
    private volatile IReadOnlyDictionary<string, IReadOnlyList<SecurityKey>> _keysByIssuer =
        new Dictionary<string, IReadOnlyList<SecurityKey>>(StringComparer.OrdinalIgnoreCase);

    public MultiAuthorityConfigurationManager(IReadOnlyCollection<string> authorities, bool requireHttps)
    {
        ArgumentNullException.ThrowIfNull(authorities);

        _inner = authorities
            .Where(static authority => !string.IsNullOrWhiteSpace(authority))
            .Select(authority => (
                Authority: authority.Trim().TrimEnd('/'),
                Manager: new ConfigurationManager<OpenIdConnectConfiguration>(
                    authority.Trim().TrimEnd('/') + "/.well-known/openid-configuration",
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever { RequireHttps = requireHttps })))
            .ToArray();
    }

    internal MultiAuthorityConfigurationManager(
        IReadOnlyList<(string Authority, OpenIdConnectConfiguration Configuration)> seeded)
    {
        ArgumentNullException.ThrowIfNull(seeded);
        _seeded = seeded.ToArray();
        _inner = [];
    }

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        if (_seeded is not null)
        {
            return Merge(_seeded);
        }

        var sources = new List<(string Authority, OpenIdConnectConfiguration Configuration)>(_inner.Length);
        Exception? firstFailure = null;

        foreach (var (authority, manager) in _inner)
        {
            OpenIdConnectConfiguration configuration;
            try
            {
                configuration = await manager.GetConfigurationAsync(cancel).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Tolerate a single unreachable IdP so the remaining providers keep
                // validating; surface the failure only when every authority fails.
                firstFailure ??= ex;
                continue;
            }

            sources.Add((authority, configuration));
        }

        if (sources.Count == 0)
        {
            throw firstFailure
                ?? new InvalidOperationException("No OIDC authorities are configured for metadata discovery.");
        }

        return Merge(sources);
    }

    internal void BindIssuerSigningKeys(TokenValidationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.TryAllIssuerSigningKeys = false;
        parameters.IssuerSigningKeyResolver = (_, securityToken, _, _) =>
            ResolveSigningKeys(securityToken?.Issuer);
    }

    // An unknown issuer returns an empty key set, never null, because a null resolver result would fall back to the merged key union.
    internal IEnumerable<SecurityKey> ResolveSigningKeys(string? issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return [];
        }

        var map = _keysByIssuer;
        var trimmed = issuer.Trim();
        if (map.TryGetValue(trimmed, out var matched))
        {
            return matched;
        }

        var withoutSlash = trimmed.TrimEnd('/');
        return map.TryGetValue(withoutSlash, out matched) ? matched : [];
    }

    public void RequestRefresh()
    {
        foreach (var (_, manager) in _inner)
        {
            manager.RequestRefresh();
        }
    }

    private OpenIdConnectConfiguration Merge(
        IReadOnlyList<(string Authority, OpenIdConnectConfiguration Configuration)> sources)
    {
        var merged = new OpenIdConnectConfiguration();
        var keysByIssuer = new Dictionary<string, List<SecurityKey>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (authority, configuration) in sources)
        {
            merged.Issuer ??= configuration.Issuer;
            var signingKeys = configuration.SigningKeys?.ToList() ?? [];
            foreach (var key in signingKeys)
            {
                merged.SigningKeys.Add(key);
            }

            // Bind to the discovery issuer and to the configured authority URL. A token's
            // iss usually matches discovery, but some providers emit the authority form.
            AddIssuerKeys(keysByIssuer, configuration.Issuer, signingKeys);
            AddIssuerKeys(keysByIssuer, authority, signingKeys);
        }

        _keysByIssuer = keysByIssuer.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<SecurityKey>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
        return merged;
    }

    private static void AddIssuerKeys(
        Dictionary<string, List<SecurityKey>> keysByIssuer,
        string? issuer,
        List<SecurityKey> signingKeys)
    {
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return;
        }

        foreach (var candidate in new[] { issuer.Trim(), issuer.Trim().TrimEnd('/') })
        {
            if (candidate.Length == 0)
            {
                continue;
            }

            if (!keysByIssuer.TryGetValue(candidate, out var list))
            {
                list = [];
                keysByIssuer[candidate] = list;
            }

            foreach (var key in signingKeys)
            {
                if (!list.Contains(key))
                {
                    list.Add(key);
                }
            }
        }
    }
}
