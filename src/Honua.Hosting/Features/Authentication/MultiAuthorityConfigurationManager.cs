// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// <see cref="IConfigurationManager{T}"/> that loads OIDC discovery metadata from
/// multiple authorities so a single JwtBearer scheme can validate API tokens issued
/// by every configured provider. Without this, the scheme's <c>Authority</c> points
/// at the first provider only and bearer tokens from the remaining providers pass
/// issuer/audience checks but fail signature validation (IDX10501: signing key not found).
/// </summary>
/// <remarks>
/// <para>
/// Each authority is backed by its own cached
/// <see cref="ConfigurationManager{OpenIdConnectConfiguration}"/>, so per-request
/// merges are in-memory once the metadata has been fetched. Individual authority
/// failures are tolerated as long as at least one authority resolves; the JwtBearer
/// handler calls <see cref="RequestRefresh"/> on key-rollover signature failures.
/// </para>
/// <para>
/// The merged document still lists every authority's signing keys, because the
/// JwtBearer handler copies <see cref="OpenIdConnectConfiguration.SigningKeys"/> onto
/// <see cref="TokenValidationParameters.IssuerSigningKeys"/>. Signature validation
/// must not use that union: one provider's key could then sign a token that names
/// another provider's issuer. <see cref="BindIssuerSigningKeys"/> installs a resolver
/// that returns only the keys discovered for the token's <c>iss</c>
/// (honua-server#6165).
/// </para>
/// </remarks>
internal sealed class MultiAuthorityConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
{
    private readonly (string Authority, ConfigurationManager<OpenIdConnectConfiguration> Manager)[] _inner;
    private readonly (string Authority, OpenIdConnectConfiguration Configuration)[]? _seeded;
    private volatile IReadOnlyDictionary<string, IReadOnlyList<SecurityKey>> _keysByIssuer =
        new Dictionary<string, IReadOnlyList<SecurityKey>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiAuthorityConfigurationManager"/> class.
    /// </summary>
    /// <param name="authorities">The OIDC authorities to merge metadata from.</param>
    /// <param name="requireHttps">Whether metadata retrieval requires HTTPS.</param>
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

    /// <summary>
    /// Initializes a manager over discovery documents that have already been fetched.
    /// Used by tests so two in-memory authorities can prove issuer-bound key selection
    /// without contacting a network IdP.
    /// </summary>
    /// <param name="seeded">Authority URL and its discovery document, in provider order.</param>
    internal MultiAuthorityConfigurationManager(
        IReadOnlyList<(string Authority, OpenIdConnectConfiguration Configuration)> seeded)
    {
        ArgumentNullException.ThrowIfNull(seeded);
        _seeded = seeded.ToArray();
        _inner = [];
    }

    /// <inheritdoc />
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

    /// <summary>
    /// Restricts signature validation to the keys of the authority whose issuer the
    /// token names. A null resolver result would fall back to the merged key union,
    /// so an unknown issuer resolves to an empty set instead.
    /// </summary>
    /// <param name="parameters">The JwtBearer token validation parameters to bind.</param>
    internal void BindIssuerSigningKeys(TokenValidationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.TryAllIssuerSigningKeys = false;
        parameters.IssuerSigningKeyResolver = (_, securityToken, _, _) =>
            ResolveSigningKeys(securityToken?.Issuer);
    }

    /// <summary>
    /// Returns the signing keys discovered for <paramref name="issuer"/>, or an empty
    /// set when the issuer is unknown. Never returns null.
    /// </summary>
    /// <param name="issuer">The token <c>iss</c> claim.</param>
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

    /// <inheritdoc />
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
