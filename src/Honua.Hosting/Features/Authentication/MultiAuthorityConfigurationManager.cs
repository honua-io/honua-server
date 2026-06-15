// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// <see cref="IConfigurationManager{T}"/> that merges OIDC discovery metadata
/// (issuer signing keys) from multiple authorities so a single JwtBearer scheme can
/// validate API tokens issued by every configured provider. Without this, the
/// scheme's <c>Authority</c> points at the first provider only and bearer tokens
/// from the remaining providers pass issuer/audience checks but fail signature
/// validation (IDX10501: signing key not found).
/// </summary>
/// <remarks>
/// Each authority is backed by its own cached
/// <see cref="ConfigurationManager{OpenIdConnectConfiguration}"/>, so per-request
/// merges are in-memory once the metadata has been fetched. Individual authority
/// failures are tolerated as long as at least one authority resolves; the JwtBearer
/// handler concatenates the merged <see cref="OpenIdConnectConfiguration.SigningKeys"/>
/// onto its token validation parameters and calls <see cref="RequestRefresh"/> on
/// key-rollover signature failures.
/// </remarks>
internal sealed class MultiAuthorityConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration>[] _inner;

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
            .Select(authority => new ConfigurationManager<OpenIdConnectConfiguration>(
                authority.TrimEnd('/') + "/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = requireHttps }))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        var merged = new OpenIdConnectConfiguration();
        Exception? firstFailure = null;
        var resolvedAny = false;

        foreach (var manager in _inner)
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

            resolvedAny = true;
            merged.Issuer ??= configuration.Issuer;

            foreach (var key in configuration.SigningKeys)
            {
                merged.SigningKeys.Add(key);
            }
        }

        if (!resolvedAny)
        {
            throw firstFailure
                ?? new InvalidOperationException("No OIDC authorities are configured for metadata discovery.");
        }

        return merged;
    }

    /// <inheritdoc />
    public void RequestRefresh()
    {
        foreach (var manager in _inner)
        {
            manager.RequestRefresh();
        }
    }
}
