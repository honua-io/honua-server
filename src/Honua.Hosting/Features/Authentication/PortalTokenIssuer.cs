// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Exceptions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Authentication;

internal sealed partial class PortalTokenIssuer(
    ISecurityTokenStore tokens,
    ILogger<PortalTokenIssuer> logger,
    IServiceProvider? serviceProvider = null) : IPortalTokenIssuer
{
    internal const string AuthSchemeName = "PortalToken";
    internal const string AuthTypeClaimValue = "portal-token";
    internal const string TenantClaimType = "tenant_id";
    internal const string BindingClaimType = "portal_token_binding";


    private readonly ILogger<PortalTokenIssuer> _logger = logger;
    private readonly IServiceProvider? _serviceProvider = serviceProvider;

    public async Task<PortalTokenIssuance> IssueAsync(
        PortalTokenIssueRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = request.Source ?? PortalCredentialSource.None;

        // A derived credential may never outlive the credential it was derived from, so the
        // requested lifetime (already clamped to the configured maximum by the caller) is
        // clamped again to the source's own expiry (SEC-9).
        var expiresAt = source.ExpiresAt is { } sourceExpiresAt && sourceExpiresAt < request.ExpiresAt
            ? sourceExpiresAt
            : request.ExpiresAt;

        var token = CreateTokenValue();
        var record = new PortalTokenRecord
        {
            PrincipalId = request.PrincipalId,
            DisplayName = request.DisplayName,
            TenantId = request.TenantId,
            Roles = request.Roles.ToArray(),
            RolesRequireClaimsMappingEntitlement = request.RolesRequireClaimsMappingEntitlement,
            RolesDependOnlyOnRoleClaimSelection = request.RolesDependOnlyOnRoleClaimSelection,
            RolesWithoutClaimsMapping = request.RolesWithoutClaimsMapping?.ToArray(),
            TenantRequiresClaimsMappingEntitlement = request.TenantRequiresClaimsMappingEntitlement,
            ClientType = request.ClientType,
            BindingValue = NormalizeBindingValue(request.ClientType, request.BindingValue),
            AcceptMissingReferer = request.ClientType == PortalTokenClientType.Referer && request.AcceptMissingReferer,
            ExpiresAt = expiresAt,
            Source = new PortalTokenSourceRecord
            {
                Kind = source.Kind,
                Reference = source.Reference,
                Version = source.Version,
                ExpiresAt = source.ExpiresAt,
            }
        };

        await SetAsync(token, record, expiresAt, cancellationToken).ConfigureAwait(false);

        return new PortalTokenIssuance(token, expiresAt);
    }

    public async Task<PortalTokenValidation?> ValidateAsync(
        string token,
        PortalTokenBinding binding,
        CancellationToken cancellationToken)
    {
        var record = await RestoreBoundRecordAsync(token, binding, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var principal = ProjectPrincipal(record, ResolveRoles(record));
        return new PortalTokenValidation(principal, record.ExpiresAt);
    }

    public async Task<PortalTokenExchange?> ExchangeAsync(
        PortalTokenExchangeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if ((request.ClientType is null && request.BindingValue is not null) ||
            (request.ClientType is not null && string.IsNullOrWhiteSpace(request.BindingValue)))
        {
            return null;
        }

        // The presented token authenticates the exchange, so it passes exactly the checks a
        // request presenting it anywhere else passes, binding included (#5491 review).
        var presented = await RestoreBoundRecordAsync(
            request.Token,
            request.PresentedBinding,
            cancellationToken).ConfigureAwait(false);
        if (presented is null)
        {
            return null;
        }

        // ArcGIS federated-server exchanges may send only token and serverUrl.
        // In that case the new server token keeps the validated portal token's
        // binding; never infer a new binding from an unauthenticated request.
        var clientType = request.ClientType ?? presented.ClientType;
        var bindingValue = request.ClientType is null
            ? presented.BindingValue
            : request.BindingValue!;

        // Derive from the stored record, not from a reduced view of it: the persisted roles,
        // their claims-mapping provenance and fallback, the tenant's provenance and the backing
        // credential all carry over, so the new token is revalidated on every restore exactly as
        // the presented one is for backing-credential and entitlement changes (#5491 review).
        var expiresAt = request.ExpiresAt < presented.ExpiresAt ? request.ExpiresAt : presented.ExpiresAt;
        var token = CreateTokenValue();
        var record = new PortalTokenRecord
        {
            PrincipalId = presented.PrincipalId,
            DisplayName = presented.DisplayName,
            TenantId = presented.TenantId,
            Roles = presented.Roles,
            RolesRequireClaimsMappingEntitlement = presented.RolesRequireClaimsMappingEntitlement,
            RolesDependOnlyOnRoleClaimSelection = presented.RolesDependOnlyOnRoleClaimSelection,
            RolesWithoutClaimsMapping = presented.RolesWithoutClaimsMapping,
            TenantRequiresClaimsMappingEntitlement = presented.TenantRequiresClaimsMappingEntitlement,
            ClientType = clientType,
            BindingValue = NormalizeBindingValue(clientType, bindingValue),
            AcceptMissingReferer = clientType == PortalTokenClientType.Referer && presented.AcceptMissingReferer,
            ExpiresAt = expiresAt,
            Source = presented.Source,
        };

        await SetAsync(token, record, expiresAt, cancellationToken).ConfigureAwait(false);

        return new PortalTokenExchange(
            new PortalTokenIssuance(token, expiresAt),
            presented.PrincipalId,
            presented.TenantId,
            clientType);
    }

    public async Task<PortalTokenIntrospection?> IntrospectAsync(
        string tokenReference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tokenReference))
        {
            return null;
        }

        // Introspection deliberately skips the binding check (the introspecting party
        // is not the bound client); active/expired/revoked is decided by cache
        // presence + lifetime, which GetAsync already enforces (it evicts and returns
        // null for an expired entry).
        var record = await GetAsync(tokenReference, cancellationToken).ConfigureAwait(false);

        if (record is null)
        {
            return null;
        }

        // Introspection must agree with ValidateAsync: a token that would be refused there is
        // not active here either (honua-server#2997 review).
        if (!ClaimsMappingTenantAllowed(record))
        {
            return null;
        }

        if (!await SourceCredentialStillValidAsync(record, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PortalTokenIntrospection(
            record.PrincipalId,
            ResolveRoles(record),
            record.TenantId,
            record.ExpiresAt);
    }

    public async Task RevokeAsync(string tokenReference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tokenReference))
        {
            // RFC 7009 §2.2: revoking an invalid/unknown token is a no-op success.
            return;
        }

        // Removing the cache entry is the single authoritative revocation point: the
        // request-path validator and introspection both resolve to this entry, so a
        // revoked token is rejected on the very next request across every replica.
        // A shared-store delete that cannot be confirmed must fail the revocation;
        // swallowing it reports success while every replica keeps honouring the token
        //.
        await RevokeStoredAsync(tokenReference, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PortalTokenRecord?> RestoreBoundRecordAsync(
        string token,
        PortalTokenBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var record = await GetAsync(token, cancellationToken).ConfigureAwait(false);

        if (record is null)
        {
            return null;
        }

        if (!BindingMatches(record, binding))
        {
            var requestBindingValue = record.ClientType switch
            {
                PortalTokenClientType.Referer => binding.Referer,
                PortalTokenClientType.Ip => binding.ClientIp,
                _ => null,
            };
            PortalTokenLog.BindingMismatch(
                _logger,
                record.ClientType.ToString(),
                LogValueRedactor.Hash(token),
                !string.IsNullOrWhiteSpace(requestBindingValue),
                LogValueRedactor.Hash(record.BindingValue),
                LogValueRedactor.Hash(NormalizeBindingValue(record.ClientType, requestBindingValue ?? string.Empty)));
            return null;
        }

        if (!ClaimsMappingTenantAllowed(record))
        {
            // The tenant itself can no longer be validated, so the token is refused rather than
            // degraded. Dropping just the tenant would re-evaluate the caller against whatever
            // scope an absent tenant resolves to, which is not reliably narrower than the one
            // it was issued for (honua-server#2997 review).
            PortalTokenLog.ClaimsMappingTenantNoLongerEntitled(_logger, LogValueRedactor.Hash(token));
            return null;
        }

        if (!await SourceCredentialStillValidAsync(record, cancellationToken).ConfigureAwait(false))
        {
            PortalTokenLog.SourceCredentialNoLongerValid(_logger, LogValueRedactor.Hash(token));
            return null;
        }

        return record;
    }

    private static string CreateTokenValue()
    {
        // 256 bits of entropy matches AdminAuthSessionStore. Hex output keeps the
        // value URL-safe so it can flow through the ?token= query parameter that
        // ArcGIS clients use.
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private static string NormalizeBindingValue(PortalTokenClientType clientType, string bindingValue)
    {
        if (string.IsNullOrWhiteSpace(bindingValue))
        {
            return string.Empty;
        }

        return clientType switch
        {
            PortalTokenClientType.Referer => NormalizeRefererValue(bindingValue),
            PortalTokenClientType.Ip => bindingValue.Trim(),
            _ => bindingValue.Trim(),
        };
    }

    private static string NormalizeRefererValue(string value)
    {
        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            // ArcGIS binds at the host level: keep scheme + host + port so a token
            // issued for https://app.example.com/foo continues to work for
            // https://app.example.com/bar without leaking through other origins.
            var builder = new UriBuilder(uri.Scheme, uri.Host)
            {
                Port = uri.IsDefaultPort ? -1 : uri.Port,
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };
            return builder.Uri.ToString().TrimEnd('/');
        }

        return trimmed.TrimEnd('/');
    }

    private static bool BindingMatches(PortalTokenRecord record, PortalTokenBinding binding)
    {
        return record.ClientType switch
        {
            PortalTokenClientType.Bearer => true,
            // generateToken desktop clients omit Referer after minting client=referer
            // (honua-server#5558). That exception is stored on the record. Browser
            // OAuth tokens use the same client type and still require the header.
            PortalTokenClientType.Referer => (record.AcceptMissingReferer && string.IsNullOrWhiteSpace(binding.Referer))
                || string.Equals(
                    record.BindingValue,
                    NormalizeRefererValue(binding.Referer ?? string.Empty),
                    StringComparison.OrdinalIgnoreCase),
            PortalTokenClientType.Ip => string.Equals(
                record.BindingValue,
                (binding.ClientIp ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    // Roles produced under entitlement-gated mapping, and roles whose provenance is unknown, are re-checked against the live entitlement on every restore.
    private string[] ResolveRoles(PortalTokenRecord record)
    {
        // false — this issuer stamped it and the roles owe nothing to claims mapping.
        if (record.RolesRequireClaimsMappingEntitlement == false)
        {
            return record.Roles;
        }

        // true (mapping-derived) or null (persisted before the field existed, so provenance is
        // unknown) both require the live entitlement. Treating null as "not mapping-derived"
        // would let a pre-upgrade token keep its custom-mapped roles forever
        // (honua-server#2997 review).
        var requiredEntitlement = record.RolesDependOnlyOnRoleClaimSelection
            ? FeatureCatalog.OidcAuthenticationKey
            : FeatureCatalog.OidcClaimsMappingKey;
        var mappingEntitled = _serviceProvider is not null
            && LicenseGate.IsEntitlementActive(_serviceProvider, requiredEntitlement);
        return mappingEntitled
            ? record.Roles
            : record.RolesWithoutClaimsMapping ?? [];
    }

    // A token with no source, or a source that cannot be re-checked, is refused.
    private async ValueTask<bool> SourceCredentialStillValidAsync(
        PortalTokenRecord record,
        CancellationToken cancellationToken)
    {
        if (record.Source is not { } source)
        {
            return false;
        }

        if (source.Kind == PortalCredentialSourceKind.None)
        {
            return true;
        }

        var validator = _serviceProvider?.GetService<IPortalTokenSourceValidator>();
        if (validator is null)
        {
            return false;
        }

        return await validator.IsStillValidAsync(source, cancellationToken).ConfigureAwait(false);
    }

    private bool ClaimsMappingTenantAllowed(PortalTokenRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.TenantId) ||
            record.TenantRequiresClaimsMappingEntitlement == false)
        {
            return true;
        }

        return _serviceProvider is not null
            && LicenseGate.IsEntitlementActive(_serviceProvider, FeatureCatalog.OidcClaimsMappingKey);
    }

    private static ClaimsPrincipal ProjectPrincipal(
        PortalTokenRecord record,
        string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, record.PrincipalId),
            new(ClaimTypes.NameIdentifier, record.PrincipalId),
            new("auth_type", AuthTypeClaimValue),
            new(BindingClaimType, record.ClientType.ToString()),
        };

        if (!string.IsNullOrWhiteSpace(record.DisplayName))
        {
            claims.Add(new Claim("display_name", record.DisplayName));
        }

        if (!string.IsNullOrWhiteSpace(record.TenantId))
        {
            claims.Add(new Claim(TenantClaimType, record.TenantId!));
        }

        foreach (var role in roles.Where(role => !string.IsNullOrWhiteSpace(role)))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(
            claims,
            AuthSchemeName,
            ClaimTypes.Name,
            ClaimTypes.Role);

        // auth_type and the client binding are projected from the server-side token
        // record, never from the presented token value, so mark them framework-owned
        // and they survive shared claim sanitization.
        CanonicalSecurityActor.StampAuthorityClaims(identity);

        return new ClaimsPrincipal(identity);
    }

    // A token another node will not see must not look issued, so a store failure reaches the caller.
    private Task SetAsync(string token, PortalTokenRecord record, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        => tokens.StoreAsync(
            Key(token),
            JsonSerializer.SerializeToUtf8Bytes(record, PortalTokenJsonContext.Default.PortalTokenRecord),
            expiresAt - DateTimeOffset.UtcNow,
            cancellationToken);

    private async Task<PortalTokenRecord?> GetAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var stored = await tokens.FindAsync(Key(token), cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        var record = JsonSerializer.Deserialize(stored.Payload, PortalTokenJsonContext.Default.PortalTokenRecord);
        return record is null || record.ExpiresAt <= DateTimeOffset.UtcNow ? null : record;
    }

    // A delete that cannot be confirmed must fail the revocation.
    private async Task RevokeStoredAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            await tokens.RevokeAsync(Key(token), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            throw new ServiceUnavailableException("The shared portal token could not be revoked.", storeFailure);
        }
    }

    private static SecurityTokenKey Key(string token) => new(SecurityTokenKind.PortalToken, token);
}

internal sealed class PortalTokenRecord
{
    public required string PrincipalId { get; init; }

    public string? DisplayName { get; init; }

    public string? TenantId { get; init; }

    public required string[] Roles { get; init; }

    // This flag is nullable because a missing value means unknown provenance and fails closed; a default false would let mapped roles outlive the entitlement.
    public bool? RolesRequireClaimsMappingEntitlement { get; init; }

    // A missing value revalidates against identity.claims-mapping, the stricter of the two entitlements.
    public bool RolesDependOnlyOnRoleClaimSelection { get; init; }

    // Null fallback roles mean unknown and stay fail-closed; an empty array is a known empty fallback.
    public string[]? RolesWithoutClaimsMapping { get; init; }

    public bool? TenantRequiresClaimsMappingEntitlement { get; init; }

    public required PortalTokenClientType ClientType { get; init; }

    public required string BindingValue { get; init; }

    public bool AcceptMissingReferer { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    // A missing source means unknown provenance, not the absence of a credential, and unknown fails closed.
    public PortalTokenSourceRecord? Source { get; init; }
}

// The persisted source stores identifiers and non-reversible markers only, never credential material.
internal sealed class PortalTokenSourceRecord
{
    public required PortalCredentialSourceKind Kind { get; init; }

    public string? Reference { get; init; }

    public string? Version { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PortalTokenRecord))]
internal sealed partial class PortalTokenJsonContext : JsonSerializerContext
{
}
