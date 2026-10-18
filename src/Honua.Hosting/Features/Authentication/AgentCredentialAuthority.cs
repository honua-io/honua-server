// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>A dedicated issuer/client audience reserved for workloads, never human role authorization.</summary>
public sealed class OidcDelegatedAgentAudience
{
    /// <summary>Exact validated issuer reserved for delegated workloads.</summary>
    public string Issuer { get; set; } = string.Empty;
    /// <summary>Dedicated client audience; keep registered when retiring individual profiles.</summary>
    public string Audience { get; set; } = string.Empty;
}

/// <summary>A host-configured delegation ceiling for one issuer-scoped managed identity.</summary>
public sealed class OidcDelegatedAgentProfile
{
    /// <summary>Stable, unique host-owned profile identifier.</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>Increment on any authority or binding change to invalidate pending approvals.</summary>
    public long Generation { get; set; }
    /// <summary>Whether delegation is enabled.</summary>
    public bool Enabled { get; set; }
    /// <summary>Exact validated token issuer.</summary>
    public string Issuer { get; set; } = string.Empty;
    /// <summary>Exact validated token audience.</summary>
    public string Audience { get; set; } = string.Empty;
    /// <summary>Exact case-sensitive token subject.</summary>
    public string Subject { get; set; } = string.Empty;
    /// <summary>Exact tenant claim value.</summary>
    public string Tenant { get; set; } = string.Empty;
    /// <summary>Validated token claim containing the tenant; never a request header.</summary>
    public string TenantClaimType { get; set; } = "tenant_id";
    /// <summary>OAuth scope required on the validated access token.</summary>
    public string RequiredScope { get; set; } = "agent:workspace";
    /// <summary>Delegation expiry, independent of token expiry.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>Maximum permissions, intersected with current canonical role grants.</summary>
    public string[] Permissions { get; set; } = [];
}

/// <summary>Discriminated, sealed approval credential binding; contains no bearer secret.</summary>
public sealed record AgentCredentialSnapshot
{
    /// <summary>Credential kind: api-key or oidc-delegated-agent.</summary>
    public required string Kind { get; init; }
    /// <summary>Stored API key ID or delegation profile ID.</summary>
    public required string Id { get; init; }
    /// <summary>Authentication-time stored credential version; no secret key material.</summary>
    public string? CredentialVersion { get; init; }
    /// <summary>Delegation generation captured at proposal time.</summary>
    public long Generation { get; init; }
    /// <summary>Exact issuer, audience, subject and tenant captured at proposal time.</summary>
    public string[] Binding { get; init; } = [];
    /// <summary>Canonical role-grant hashes captured at proposal time.</summary>
    public string[] RoleGrantFingerprints { get; init; } = [];
    /// <summary>Canonical managed roles captured at enrollment resolution.</summary>
    public string[] MembershipRoles { get; init; } = [];
    /// <summary>Validated bearer token expiry; replay cannot outlive its originating credential.</summary>
    public DateTimeOffset TokenExpiresAt { get; init; }
    /// <summary>Canonical actor captured at proposal time.</summary>
    public string? ActorId { get; init; }
    /// <summary>Granted permissions captured at proposal time.</summary>
    public required string[] Permissions { get; init; }
}

internal static class AgentCredentialAuthority
{
    internal const string ValidationClaim = "honua:delegated_agent_validation";
    private static readonly string[] ValidationClaimTypes = ["iss", "sub", "aud", "exp", "tenant_id", "tid", "scope", "scp", ClaimTypes.NameIdentifier];
    private const string GrantClaim = "honua:delegated_agent_acl";
    private const string DelegatedOnlyClaim = "honua:delegated_agent_only";
    private const string GenerationClaim = "honua:delegated_agent_generation";
    private const string ProfileClaim = "honua:delegated_agent_profile";
    internal const string DelegatedRole = "delegated-agent";
    private const string DelegatedKind = "oidc-delegated-agent";

    // Called only by JwtBearer's successful validation event, before custom mappings.
    internal static void MarkValidated(ClaimsIdentity identity, IServiceProvider? services = null, string? validatedIssuer = null)
    {
        foreach (var existing in identity.FindAll(ValidationClaim).ToArray()) identity.RemoveClaim(existing);
        var marker = CanonicalSecurityActor.CreateStampedClaim(ValidationClaim, "validated");
        if (validatedIssuer is not null) marker.Properties["validated-issuer"] = validatedIssuer;
        var types = ValidationClaimTypes
            .Concat(services is null ? [] : Profiles(services).Select(p => p.TenantClaimType));
        foreach (var type in types.Distinct(StringComparer.Ordinal))
        {
            var values = identity.FindAll(type).ToArray();
            marker.Properties["count:" + type] = values.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Ambiguous identities fail closed, including duplicate equal values.
            if (values.Length == 1) marker.Properties[type] = values[0].Value;
        }
        if (!marker.Properties.ContainsKey("sub"))
        {
            var subjects = identity.FindAll(ClaimTypes.NameIdentifier).ToArray();
            if (!identity.HasClaim(c => c.Type == "sub") && subjects.Length == 1) marker.Properties["sub"] = subjects[0].Value;
        }
        var audiences = identity.FindAll("aud").ToArray();
        marker.Properties["aud-count"] = audiences.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < audiences.Length; i++) marker.Properties["audience-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = audiences[i].Value;
        identity.AddClaim(marker);
    }

    internal static bool IsAgent(ClaimsPrincipal principal) => principal.Identity?.IsAuthenticated == true &&
        !ApiKeyAuthenticationHandler.IsTrustedNonStoredOperator(principal) &&
        principal.HasClaim("permission", "agent:workspace") &&
        (principal.HasClaim(c => c.Type == "api_key_id") || TrustedProfile(principal) is not null);

    internal static async Task<ClaimsPrincipal> BindOidcAsync(ClaimsPrincipal principal, IServiceProvider? services)
    {
        if (services is null || principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity) return principal;
        var marker = principal.FindAll(ValidationClaim).SingleOrDefault(CanonicalSecurityActor.IsFrameworkOwnedClaim);
        if (marker is null) return principal;
        var validatedIssuer = Value(marker, "validated-issuer") ?? Value(marker, "iss");
        var reserved = Audiences(services).Any(a => a.Issuer == validatedIssuer &&
            marker.Properties.Any(property => property.Key.StartsWith("audience-", StringComparison.Ordinal) && property.Value == a.Audience));
        var dedicatedAudience = reserved;
        var knownSubject = Profiles(services).Any(p => p.Issuer == Value(marker, "iss") && p.Subject == Value(marker, "sub"));
        if (!dedicatedAudience && !knownSubject) return principal;
        // Quarantine workloads before eligibility checks: a removed/disabled profile may
        // never fall back to the managed account's ordinary admin or data roles.
        ProjectRoles(identity, services);
        foreach (var claim in identity.FindAll("permission").Concat(identity.FindAll(ProfileClaim)).ToArray()) identity.RemoveClaim(claim);
        if (!reserved || !long.TryParse(Value(marker, "exp"), out var expiry) ||
            expiry <= Now(services).ToUnixTimeSeconds()) return principal;
        var profiles = Profiles(services).Where(p => Valid(p, services) &&
            !HasAmbiguousBinding(marker, p.TenantClaimType) &&
            p.Issuer == Value(marker, "iss") && p.Subject == Value(marker, "sub") &&
            (Value(marker, "tenant_id") is null || Value(marker, "tenant_id") == p.Tenant) &&
            (Value(marker, "tid") is null || Value(marker, "tid") == p.Tenant) &&
            p.Tenant == Value(marker, p.TenantClaimType) && p.Audience == Value(marker, "aud") &&
            (Value(marker, "scope") ?? Value(marker, "scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(p.RequiredScope, StringComparer.Ordinal)).ToArray();
        if (profiles.Length != 1) return principal;
        var profile = profiles[0];
        if (CanonicalSecurityActor.Resolve(principal)?.SubjectId != profile.Subject) return principal;
        var authority = await ResolveAuthorityAsync(profile, services, default).ConfigureAwait(false);
        var permissions = authority.Permissions;
        if (!permissions.Contains("agent:workspace", StringComparer.Ordinal)) return principal;
        foreach (var claim in identity.FindAll("permission").Concat(identity.FindAll(ProfileClaim)).ToArray()) identity.RemoveClaim(claim);
        foreach (var permission in permissions) identity.AddClaim(CanonicalSecurityActor.CreateStampedClaim("permission", permission));
        ProjectRoles(identity, services);
        var binding = CanonicalSecurityActor.CreateStampedClaim(ProfileClaim, profile.Id);
        binding.Properties["generation"] = profile.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        binding.Properties["expiry"] = Value(marker, "exp")!;
        binding.Properties["issuer"] = profile.Issuer;
        binding.Properties["audience"] = profile.Audience;
        binding.Properties["subject"] = profile.Subject;
        binding.Properties["tenant"] = profile.Tenant;
        binding.Properties["tenant-claim"] = profile.TenantClaimType;
        binding.Properties["required-scope"] = profile.RequiredScope;
        for (var i = 0; i < authority.Roles.Length; i++) binding.Properties["role-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = authority.Roles[i];
        AddGrantClaims(identity, authority.Grants);
        identity.AddClaim(CanonicalSecurityActor.CreateStampedClaim(GenerationClaim, profile.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        identity.AddClaim(binding);
        return principal;
    }

    internal static async Task<AgentCredentialSnapshot?> CaptureAsync(ClaimsPrincipal principal, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!IsAgent(principal)) return null;
        var binding = TrustedProfile(principal);
        var snapshot = binding is null
            ? new AgentCredentialSnapshot { Kind = "api-key", Id = principal.FindFirst("api_key_id")!.Value, CredentialVersion = StoredApiKeyCredentialVersion.GetStamped(principal), Permissions = principal.FindAll("permission").Select(c => c.Value).ToArray() }
            : new AgentCredentialSnapshot { Kind = DelegatedKind, Id = binding.Value, Generation = long.TryParse(Value(binding, "generation"), out var generation) ? generation : 0,
                Binding = [Value(binding, "issuer") ?? "", Value(binding, "audience") ?? "", Value(binding, "subject") ?? "", Value(binding, "tenant") ?? "", Value(binding, "tenant-claim") ?? "", Value(binding, "required-scope") ?? ""],
                RoleGrantFingerprints = principal.FindAll(GrantClaim).Where(CanonicalSecurityActor.IsFrameworkOwnedClaim).Select(c => c.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                MembershipRoles = binding.Properties.Where(p => p.Key.StartsWith("role-", StringComparison.Ordinal)).Select(p => p.Value ?? "").Order(StringComparer.Ordinal).ToArray(),
                TokenExpiresAt = long.TryParse(Value(binding, "expiry"), out var expiry) ? DateTimeOffset.FromUnixTimeSeconds(expiry) : DateTimeOffset.MinValue,
                ActorId = CanonicalSecurityActor.Resolve(principal)?.ActorId, Permissions = principal.FindAll("permission").Select(c => c.Value).ToArray() };
        var current = await RevalidateAsync(snapshot, services, cancellationToken).ConfigureAwait(false);
        return current is not null && CanonicalSecurityActor.Resolve(current)?.ActorId == CanonicalSecurityActor.Resolve(principal)?.ActorId ? snapshot : null;
    }

    internal static async Task<ClaimsPrincipal?> RevalidateAsync(AgentCredentialSnapshot snapshot, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (snapshot.Kind == "api-key")
        {
            if (!Guid.TryParse(snapshot.Id, out var id) || id == Guid.Empty) return null;
            var key = await services.GetRequiredService<IAdminApiKeyStore>().GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (key is null || snapshot.CredentialVersion is null || snapshot.CredentialVersion != StoredApiKeyCredentialVersion.FromRecord(key) || key.RevokedAt is not null || key.ExpiresAt <= Now(services) || !Same(key.Permissions, snapshot.Permissions)) return null;
            return ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(key);
        }
        if (snapshot.Kind != DelegatedKind || snapshot.TokenExpiresAt <= Now(services)) return null;
        var matches = Profiles(services).Where(p => p.Id == snapshot.Id).ToArray();
        if (matches.Length != 1 || !Valid(matches[0], services) || matches[0].Generation != snapshot.Generation) return null;
        var profile = matches[0];
        if (!snapshot.Binding.SequenceEqual([profile.Issuer, profile.Audience, profile.Subject, profile.Tenant, profile.TenantClaimType, profile.RequiredScope])) return null;
        var authority = await ResolveAuthorityAsync(profile, services, cancellationToken).ConfigureAwait(false);
        var permissions = authority.Permissions;
        if (!Same(authority.Roles, snapshot.MembershipRoles) || !Same(authority.Grants.Select(GrantFingerprint).Distinct(StringComparer.Ordinal), snapshot.RoleGrantFingerprints)) return null;
        if (!permissions.Contains("agent:workspace", StringComparer.Ordinal) || !Same(permissions, snapshot.Permissions)) return null;
        var identity = new ClaimsIdentity([new Claim("iss", profile.Issuer), new Claim("sub", profile.Subject), new Claim(profile.TenantClaimType, profile.Tenant), new Claim("aud", profile.Audience)], OidcAuthenticationExtensions.JwtBearerScheme);
        foreach (var permission in permissions) identity.AddClaim(CanonicalSecurityActor.CreateStampedClaim("permission", permission));
        ProjectRoles(identity, services);
        var binding = CanonicalSecurityActor.CreateStampedClaim(ProfileClaim, profile.Id);
        binding.Properties["generation"] = profile.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        binding.Properties["issuer"] = profile.Issuer;
        binding.Properties["audience"] = profile.Audience;
        binding.Properties["subject"] = profile.Subject;
        binding.Properties["tenant"] = profile.Tenant;
        binding.Properties["tenant-claim"] = profile.TenantClaimType;
        binding.Properties["required-scope"] = profile.RequiredScope;
        for (var i = 0; i < authority.Roles.Length; i++) binding.Properties["role-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = authority.Roles[i];
        identity.AddClaim(CanonicalSecurityActor.CreateStampedClaim(GenerationClaim, profile.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        AddGrantClaims(identity, authority.Grants);
        binding.Properties["expiry"] = snapshot.TokenExpiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        identity.AddClaim(binding);
        var principal = new ClaimsPrincipal(identity);
        return CanonicalSecurityActor.Resolve(principal)?.ActorId == snapshot.ActorId ? principal : null;
    }

    /// <summary>Rechecks any stored key or delegated workload; human schemes keep their own live policy.</summary>
    internal static async Task<bool> IsCurrentAsync(ClaimsPrincipal principal, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (principal.Identity?.IsAuthenticated != true) return false;
        if (ApiKeyAuthenticationHandler.IsTrustedNonStoredOperator(principal)) return true;
        if (IsDelegated(principal))
        {
            if (services.GetService<ITenantContext>() is { } tenant && !MatchesTenant(principal, tenant.TenantId)) return false;
            return await CaptureAsync(principal, services, cancellationToken).ConfigureAwait(false) is not null;
        }
        if (principal.FindFirst("api_key_id") is not { } keyId) return true;
        var snapshot = new AgentCredentialSnapshot { Kind = "api-key", Id = keyId.Value, CredentialVersion = StoredApiKeyCredentialVersion.GetStamped(principal),
            Permissions = principal.FindAll("permission").Select(c => c.Value).ToArray() };
        var current = await RevalidateAsync(snapshot, services, cancellationToken).ConfigureAwait(false);
        return current is not null && CanonicalSecurityActor.Resolve(current)?.ActorId == CanonicalSecurityActor.Resolve(principal)?.ActorId;
    }

    internal static bool MatchesTenant(ClaimsPrincipal principal, string? tenant) => !IsDelegated(principal) ||
        TrustedProfile(principal) is { } profile && Value(profile, "tenant") == tenant;

    internal static bool AuthorizesCanonicalGrant(ClaimsPrincipal principal, string service, string? layer, AuthorizationOperation operation)
    {
        if (!IsAgent(principal) || !IsDelegated(principal)) return false;
        var grants = principal.FindAll(GrantClaim).Where(CanonicalSecurityActor.IsFrameworkOwnedClaim).Select(claim => new PermissionGrant
        {
            Service = Value(claim, "service") ?? "", Layer = Value(claim, "layer") ?? "", Operation = Value(claim, "operation") ?? ""
        }).ToArray();
        return PermissionResolver.AuthorizeSnapshot(new EffectivePermissions
        {
            UserId = principal.FindFirst("sub")?.Value ?? "", Roles = SourceRoles(principal), Permissions = grants
        }, service, layer, operation).IsAllowed;
    }

    internal static string[] SourceRoles(ClaimsPrincipal principal) => TrustedProfile(principal)?.Properties
        .Where(p => p.Key.StartsWith("role-", StringComparison.Ordinal)).Select(p => p.Value ?? "").ToArray() ?? [];

    private static string GrantFingerprint(PermissionGrant grant) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{grant.Service.Length}:{grant.Service}{grant.Layer.Length}:{grant.Layer}{grant.Operation.Length}:{grant.Operation}")));

    private static void AddGrantClaims(ClaimsIdentity identity, IReadOnlyList<PermissionGrant> grants)
    {
        foreach (var grant in grants)
        {
            var claim = CanonicalSecurityActor.CreateStampedClaim(GrantClaim, GrantFingerprint(grant));
            claim.Properties["service"] = grant.Service;
            claim.Properties["layer"] = grant.Layer;
            claim.Properties["operation"] = grant.Operation;
            identity.AddClaim(claim);
        }
    }

    internal static bool IsDelegated(ClaimsPrincipal principal) => principal.FindAll(DelegatedOnlyClaim).Any(CanonicalSecurityActor.IsFrameworkOwnedClaim);
    private static string? Value(Claim claim, string key) => claim.Properties.TryGetValue(key, out var value) ? value : null;
    private static void ProjectRoles(ClaimsIdentity identity, IServiceProvider services)
    {
        foreach (var claim in identity.Claims.Where(c => c.Type == identity.RoleClaimType || c.Type == ClaimTypes.Role ||
            c.Type == services.GetService<IOptions<RbacOptions>>()?.Value.EffectiveRoleClaimType ||
            c.Type is "role" or "roles" || c.Type.Contains("role", StringComparison.OrdinalIgnoreCase)).ToArray()) identity.RemoveClaim(claim);
        foreach (var marker in identity.FindAll(DelegatedOnlyClaim).Concat(identity.FindAll(GenerationClaim)).Concat(identity.FindAll(GrantClaim)).ToArray()) identity.RemoveClaim(marker);
        identity.AddClaim(CanonicalSecurityActor.CreateStampedClaim(DelegatedOnlyClaim, "1"));
        identity.AddClaim(new Claim(ClaimTypes.Role, DelegatedRole));
    }
    private static Claim? TrustedProfile(ClaimsPrincipal principal) => principal.FindAll(ProfileClaim).FirstOrDefault(CanonicalSecurityActor.IsFrameworkOwnedClaim);
    private static OidcDelegatedAgentAudience[] Audiences(IServiceProvider services) => services.GetService<IOptionsMonitor<OidcAuthenticationOptions>>()?.CurrentValue.DelegatedAgentAudiences ?? [];
    private static OidcDelegatedAgentProfile[] Profiles(IServiceProvider services) => services.GetService<IOptionsMonitor<OidcAuthenticationOptions>>()?.CurrentValue.DelegatedAgents ?? [];
    private static DateTimeOffset Now(IServiceProvider services) => (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
    private static bool Same(IEnumerable<string> first, IEnumerable<string> second) => first.Order(StringComparer.Ordinal).SequenceEqual(second.Order(StringComparer.Ordinal));
    private static bool Valid(OidcDelegatedAgentProfile p, IServiceProvider services) => services.GetService<IOptionsMonitor<OidcAuthenticationOptions>>()?.CurrentValue.Enabled == true && p.Enabled && p.Generation > 0 &&
        Audiences(services).Count(a => a.Issuer == p.Issuer && a.Audience == p.Audience) == 1 &&
        !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Issuer) && !string.IsNullOrWhiteSpace(p.Audience) &&
        !string.IsNullOrWhiteSpace(p.Subject) && !string.IsNullOrWhiteSpace(p.RequiredScope) && !string.IsNullOrWhiteSpace(p.TenantClaimType) && !string.IsNullOrWhiteSpace(p.Tenant) && p.ExpiresAt > Now(services) &&
        Profiles(services).Count(other => other.Id == p.Id) == 1;

    private sealed record CurrentAuthority(string[] Permissions, string[] Roles, PermissionGrant[] Grants);

    private static async Task<CurrentAuthority> ResolveAuthorityAsync(OidcDelegatedAgentProfile profile, IServiceProvider services, CancellationToken cancellationToken)
    {
        var source = services.GetService<IPrincipalMembershipSource>();
        var store = services.GetService<IRoleStore>();
        if (source is null || store is null) return new([], [], []);
        try
        {
            var membership = await source.ResolveMembershipAsync(profile.Subject, profile.Issuer, cancellationToken).ConfigureAwait(false);
            if (membership is not { IsActive: true }) return new([], [], []);
            var effective = await store.GetEffectivePermissionsAsync(profile.Subject, membership.Roles, cancellationToken).ConfigureAwait(false);
            // Only explicit global resource/operation grants can mint a coarse permission.
            // Resource-specific and wildcard grants do not become a broader agent capability.
            return new(profile.Permissions.Where(permission => Covered(permission, effective.Permissions))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), membership.Roles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), effective.Permissions.ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new([], [], []); }
    }
    private static bool HasAmbiguousBinding(Claim marker, string tenantClaimType) =>
        Count(marker, "sub") > 1 || Count(marker, ClaimTypes.NameIdentifier) > 1 ||
        Count(marker, "tenant_id") > 1 || Count(marker, "tid") > 1 || Count(marker, tenantClaimType) > 1;

    private static int Count(Claim marker, string type) =>
        int.TryParse(Value(marker, "count:" + type), out var count) ? count : 0;

    private static bool Covered(string permission, IReadOnlyList<PermissionGrant> grants)
    {
        if (LayerScopedWriteKey.IsMalformedResourceGrant(permission)) return false;
        if (permission.StartsWith("read:", StringComparison.Ordinal) || permission.StartsWith("write:", StringComparison.Ordinal))
        {
            // This is only the configured ceiling. Every actual data operation also
            // intersects the fresh canonical role grants through AuthorizeSnapshot.
            return true;
        }
        // Coarse workspace/admin capabilities are explicit global canonical role grants.
        return permission.Split(':') is [var resource, var action] && resource is "agent" or "admin" &&
            action != "*" && grants.Any(grant => grant.Service == resource && grant.Operation == action && grant.Layer == "*");
    }

}
