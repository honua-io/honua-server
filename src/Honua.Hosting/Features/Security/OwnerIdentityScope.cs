// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Security;

// A trusted identity provider configured outside the Oidc section, recognised by the auth_type its session writer stamps.
internal sealed record ExternalIdentityProviderRegistration(string ProviderKey, string AuthType);

internal interface IExternalIdentityProviderSource
{
    IEnumerable<ExternalIdentityProviderRegistration> GetProviders();
}

// With two or more trusted identity providers one subject value can name two people, so the owner value carries the provider; a single provider keeps bare subjects.
internal sealed class OwnerIdentityScope
{
    internal const string OwnerClaim = "honua:owner";
    internal const string SubjectIssuerClaim = "honua:subject_issuer";
    private const string IssuerClaim = "iss";
    private const string LocalTag = "local";

    private readonly OidcAuthenticationOptions _oidc;
    private readonly IOidcRuntimeProviderSource? _runtimeProviders;
    private readonly ExternalIdentityProviderRegistration[] _externalProviders;

    public OwnerIdentityScope(
        IOptions<OidcAuthenticationOptions> oidcOptions,
        IEnumerable<IExternalIdentityProviderSource> externalSources,
        IOidcRuntimeProviderSource? runtimeProviders = null)
    {
        ArgumentNullException.ThrowIfNull(oidcOptions);
        ArgumentNullException.ThrowIfNull(externalSources);

        _oidc = oidcOptions.Value;
        _runtimeProviders = runtimeProviders;
        _externalProviders = externalSources.SelectMany(static source => source.GetProviders()).ToArray();
    }

    // Admin-registered OIDC providers can change at runtime, so the provider set is read per decision.
    public bool IsMultiProvider => IsMulti(TrustedProviders());

    // issuedClaims is the identity as its issuer supplied it, read before any configured claim mapping can add an iss value.
    public void StampOwner(ClaimsPrincipal principal, ClaimsIdentity issuedClaims)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(issuedClaims);

        if (principal.Identity is ClaimsIdentity identity)
        {
            CanonicalSecurityActor.StampFrameworkClaim(identity, OwnerClaim, ResolveQualifiedOwner(principal, issuedClaims));
        }
    }

    // On a multi-provider install every owner carries a tag: a provider tag for federated identities, "local" for the rest, so no bare legacy value matches.
    internal string? ResolveQualifiedOwner(ClaimsPrincipal principal, ClaimsIdentity issuedClaims)
    {
        var providers = TrustedProviders();
        if (!IsMulti(providers))
        {
            return null;
        }

        var subject = ServiceDataEditorAuthorization.ResolveBareOwnerSubject(principal);
        if (subject is null)
        {
            return null;
        }

        var provider = ResolveProvider(principal, issuedClaims, providers.ByIssuer);
        return Qualify(provider is null ? LocalTag : ProviderTag(provider), subject);
    }

    // A tag never contains '|', so the first '|' always ends it.
    internal static string Qualify(string tag, string subject) => $"{tag}|{subject}";

    // A short digest keeps editor-tracking fields narrow; the key is an operator-configured authority, not caller input.
    internal static string ProviderTag(string providerKey)
        => "idp-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(providerKey)).AsSpan(0, 6));

    // Produces an issuer- and tenant-qualified binding key for resources owned by an authenticated principal.
    internal static string? TryResolvePrincipalBindingKey(
        ClaimsPrincipal? principal,
        TenantContextOptions? tenantOptions = null)
    {
        if (principal?.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
        {
            return null;
        }

        var tenant = ResolveBindingTenantSegment(principal, tenantOptions);
        if (CanonicalSecurityActor.Resolve(principal) is { } actor)
        {
            return FormatActorBindingKey(principal, actor, tenant);
        }

        var scheme = CanonicalSecurityActor.FindStampedValue(principal, CanonicalSecurityActor.AuthenticationSchemeClaim)?.Trim().ToLowerInvariant()
            ?? identity.AuthenticationType?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(scheme))
        {
            return null;
        }

        return TryResolveIdentityBindingKey(identity, scheme, tenant);
    }

    private static string FormatActorBindingKey(
        ClaimsPrincipal principal,
        CanonicalSecurityActorIdentity actor,
        string tenant)
    {
        var subjectIssuer = CanonicalSecurityActor.FindStampedValue(principal, SubjectIssuerClaim);
        if (!string.IsNullOrWhiteSpace(subjectIssuer) && !string.IsNullOrWhiteSpace(actor.SubjectId))
        {
            return $"{actor.AuthenticationScheme}:subject:{Uri.EscapeDataString(subjectIssuer.Trim())}:{Uri.EscapeDataString(actor.SubjectId)}:tenant:{tenant}";
        }

        return $"{actor.ActorId}:tenant:{tenant}";
    }

    private static string? TryResolveIdentityBindingKey(
        ClaimsIdentity identity,
        string scheme,
        string tenant)
    {
        var apiKeyId = identity.FindFirst("api_key_id")?.Value?.Trim();
        if (!string.IsNullOrEmpty(apiKeyId))
        {
            return $"{Uri.EscapeDataString(scheme)}:api-key:{Uri.EscapeDataString(apiKeyId)}:tenant:{tenant}";
        }

        // Bootstrap admin / dev-bypass keys carry no api_key_id; bind them by auth_type and name
        // so they never collide with managed admin API keys or federated users named "admin".
        if (string.Equals(scheme, AuthenticationExtensions.ApiKeyScheme, StringComparison.OrdinalIgnoreCase))
        {
            var authType = identity.FindFirst("auth_type")?.Value?.Trim();
            var name = identity.Name?.Trim();
            if (!string.IsNullOrEmpty(authType) && !string.IsNullOrEmpty(name))
            {
                return $"{Uri.EscapeDataString(scheme)}:bootstrap:{Uri.EscapeDataString(authType)}:{Uri.EscapeDataString(name)}:tenant:{tenant}";
            }
        }

        return null;
    }

    internal static string ResolveBindingTenantSegment(
        ClaimsPrincipal principal,
        TenantContextOptions? tenantOptions = null)
    {
        var tenant = CanonicalSecurityActor.FindStampedValue(principal, CanonicalSecurityActor.EffectiveTenantClaim)
            ?? (tenantOptions is not null
                ? PlatformAdminAuthorization.ResolveTenantId(principal, tenantOptions)
                : principal.FindFirstValue("honua:tenant_id")
                  ?? principal.FindFirstValue("tenant_id")
                  ?? principal.FindFirstValue("tid")
                  ?? principal.FindFirstValue(AdminApiKeyPermission.ApprovedOperationTenantClaim));
        var trimmed = tenant?.Trim();
        return string.IsNullOrEmpty(trimmed) ? "-" : Uri.EscapeDataString(trimmed);
    }

    private string? ResolveProvider(
        ClaimsPrincipal principal,
        ClaimsIdentity issuedClaims,
        Dictionary<string, string> providerByIssuer)
    {
        var authType = CanonicalSecurityActor.FindStampedValue(principal, "auth_type");
        var sessionProvider = _externalProviders.FirstOrDefault(provider =>
            string.Equals(provider.AuthType, authType, StringComparison.OrdinalIgnoreCase));
        if (sessionProvider is not null)
        {
            return sessionProvider.ProviderKey;
        }

        var issuer = NormalizeValue(CanonicalSecurityActor.FindStampedValue(principal, SubjectIssuerClaim))
            ?? NormalizeValue(issuedClaims.FindFirst(IssuerClaim)?.Value);
        if (issuer is null)
        {
            return null;
        }

        var normalized = NormalizeIssuer(issuer);
        return providerByIssuer.TryGetValue(normalized, out var provider) ? provider : normalized;
    }

    private bool IsMulti(TrustedProviderSet providers) => providers.Count + _externalProviders.Length > 1;

    // Each valid catalog provider counts once; Entra ID v1 tokens join their v2 authority, and extra ValidIssuers are aliases of a sole provider.
    private TrustedProviderSet TrustedProviders()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!_oidc.Enabled)
        {
            return new TrustedProviderSet(map, 0);
        }

        var authorities = new List<string>();
        foreach (var provider in OidcProviderCatalog.GetProviders(_oidc, _runtimeProviders)
                     .Where(static provider => provider.IsValid && !string.IsNullOrWhiteSpace(provider.Authority)))
        {
            var authority = NormalizeIssuer(provider.Authority!);
            if (map.TryAdd(authority, authority))
            {
                authorities.Add(authority);
            }

            if (string.Equals(provider.Type, "AzureAd", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(_oidc.AzureAd?.TenantId))
            {
                map.TryAdd(NormalizeIssuer($"https://sts.windows.net/{_oidc.AzureAd.TenantId}/"), authority);
            }
        }

        foreach (var issuer in _oidc.TokenValidation.ValidIssuers.Where(static issuer => !string.IsNullOrWhiteSpace(issuer)))
        {
            var normalized = NormalizeIssuer(issuer);
            map.TryAdd(normalized, authorities.Count == 1 ? authorities[0] : normalized);
        }

        return new TrustedProviderSet(map, authorities.Count);
    }

    private sealed record TrustedProviderSet(Dictionary<string, string> ByIssuer, int Count);

    private static string NormalizeIssuer(string issuer) => issuer.Trim().TrimEnd('/');

    private static string? NormalizeValue(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
