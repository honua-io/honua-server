// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Infrastructure.Security;

namespace Honua.Infrastructure.Authentication;

// Attenuation is frozen at mint time, nothing presented at callback can widen it, and the principal uses the shared role grammar so authorization is not forked.
internal sealed partial class ScopedJobTokenIssuer(
    ISecurityTokenStore tokens,
    ILogger<ScopedJobTokenIssuer> logger) : IScopedJobTokenIssuer
{
    internal const string AuthSchemeName = "ScopedJobToken";
    internal const string AuthTypeClaimValue = "scoped-job-token";
    internal const string TenantClaimType = "tenant_id";
    internal const string JobIdClaimType = "honua_job_id";
    internal const string PermissionClaimType = "permission";
    internal const string RolesClaimType = "roles";
    private readonly ILogger<ScopedJobTokenIssuer> _logger = logger;

    public async Task<ScopedJobTokenIssuance> IssueAsync(
        ScopedJobTokenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            throw new ArgumentException("A scoped-job token must be bound to a job id.", nameof(request));
        }

        // Freeze the attenuation server-side: the effective grant is the
        // intersection of the pinned owner's reachable scope and the requested
        // resource scope. Reachability is derived from BOTH the owner's roles and
        // their per-resource permission grants, so a submitter whose write authority
        // comes purely from a write:{service}[/{layer}] grant (not a role) gets the
        // write access they legitimately hold instead of an under-scoped token. The
        // owner snapshot is the SOLE input — nothing the future job supplies
        // participates here, so the result stays strictly ⊆ the submitter.
        var effectiveScope = ScopedJobAttenuation.Intersect(
            request.Roles,
            request.Grants,
            globalDataEditorRoles: null,
            request.ResourceScope);
        var claims = ScopedJobAttenuation.ToClaims(effectiveScope);

        var token = CreateTokenValue();
        var record = new ScopedJobTokenRecord
        {
            PrincipalId = request.PrincipalId,
            // TenantId is taken from the pinned snapshot only; it is never
            // re-derived from a callback-supplied value (invariant #2).
            TenantId = request.TenantId,
            JobId = request.JobId.Trim(),
            Attempt = request.Attempt,
            Roles = claims.Roles.ToArray(),
            Permissions = claims.Permissions.ToArray(),
            ExpiresAt = request.ExpiresAt,
        };

        // A token another node will not see must not look issued, so a store failure reaches the caller.
        await tokens.StoreAsync(
            Key(token),
            JsonSerializer.SerializeToUtf8Bytes(record, ScopedJobTokenJsonContext.Default.ScopedJobTokenRecord),
            request.ExpiresAt - DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);

        ScopedJobTokenLog.TokenIssued(_logger, record.JobId, claims.Roles.Count, claims.Permissions.Count);
        return new ScopedJobTokenIssuance(token, request.ExpiresAt);
    }

    public async Task<ScopedJobTokenValidation?> ValidateAsync(
        string token,
        string jobId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        var record = await GetAsync(token, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        // Job-binding (invariant #4): the token is honored ONLY inside the exact
        // job context it was minted for. An ordinal compare prevents a token from a
        // sibling job being replayed in this job's callback context.
        if (!string.Equals(record.JobId, jobId.Trim(), StringComparison.Ordinal))
        {
            ScopedJobTokenLog.JobBindingMismatch(_logger, record.JobId, LogValueRedactor.Hash(token));
            return null;
        }

        var principal = ProjectPrincipal(record);
        return new ScopedJobTokenValidation(principal, record.JobId, record.ExpiresAt) { Attempt = record.Attempt };
    }

    public Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.CompletedTask;
        }

        return RevokeStoredAsync(token, cancellationToken);
    }

    // A delete that cannot be confirmed must not look like success.
    private async Task RevokeStoredAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            await tokens.RevokeAsync(Key(token), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            ScopedJobTokenLog.DistributedCacheRemoveFailed(_logger, LogValueRedactor.Hash(token), storeFailure);
            throw new ServiceUnavailableException("The shared job token could not be revoked.", storeFailure);
        }
    }

    private static string CreateTokenValue()
    {
        // 256 bits of entropy, matching PortalTokenIssuer / AdminAuthSessionStore.
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private static ClaimsPrincipal ProjectPrincipal(ScopedJobTokenRecord record)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, record.PrincipalId),
            new(ClaimTypes.NameIdentifier, record.PrincipalId),
            new("auth_type", AuthTypeClaimValue),
            new(JobIdClaimType, record.JobId),
        };

        if (!string.IsNullOrWhiteSpace(record.TenantId))
        {
            claims.Add(new Claim(TenantClaimType, record.TenantId!));
        }

        foreach (var role in record.Roles.Where(role => !string.IsNullOrWhiteSpace(role)))
        {
            // Project both the canonical Role claim (so IsInRole and the shared
            // role enumeration both see it) and the configurable "roles" claim
            // the RBAC pipeline also reads.
            claims.Add(new Claim(ClaimTypes.Role, role));
            claims.Add(new Claim(RolesClaimType, role));
        }

        foreach (var permission in record.Permissions.Where(permission => !string.IsNullOrWhiteSpace(permission)))
        {
            claims.Add(new Claim(PermissionClaimType, permission));
        }

        var identity = new ClaimsIdentity(
            claims,
            AuthSchemeName,
            ClaimTypes.Name,
            ClaimTypes.Role);

        // The token value carries no claims of its own: auth_type and the frozen
        // permission set come from the stored record, so mark them framework-owned
        // and they survive shared claim sanitization.
        CanonicalSecurityActor.StampAuthorityClaims(identity);

        return new ClaimsPrincipal(identity);
    }

    private async Task<ScopedJobTokenRecord?> GetAsync(string token, CancellationToken cancellationToken)
    {
        var stored = await tokens.FindAsync(Key(token), cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        var record = JsonSerializer.Deserialize(stored.Payload, ScopedJobTokenJsonContext.Default.ScopedJobTokenRecord);
        return record is null || record.ExpiresAt <= DateTimeOffset.UtcNow ? null : record;
    }

    private static SecurityTokenKey Key(string token) => new(SecurityTokenKind.JobToken, token);
}

internal sealed class ScopedJobTokenRecord
{
    public required string PrincipalId { get; init; }

    public string? TenantId { get; init; }

    public required string JobId { get; init; }

    public int Attempt { get; init; }

    public required string[] Roles { get; init; }

    public required string[] Permissions { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ScopedJobTokenRecord))]
internal sealed partial class ScopedJobTokenJsonContext : JsonSerializerContext
{
}
