// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Logging;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Distributed-cache backed <see cref="IScopedJobTokenIssuer"/> for the custom-code
/// geoprocessing auth spine (Phase 0). Mirrors <see cref="PortalTokenIssuer"/>'s
/// mint pattern — opaque entropy plus a redis-authoritative record re-hydrated into
/// a <see cref="ClaimsPrincipal"/> — but the stored record additionally carries the
/// bound <c>JobId</c> and the <em>frozen, already-intersected</em> resource scope.
/// </summary>
/// <remarks>
/// <para>
/// The attenuation is computed once, here, at mint time: the issuer intersects the
/// pinned owner snapshot's roles/grants with the requested scope
/// (<see cref="ScopedJobAttenuation.Intersect"/>) and stores only the resulting
/// role/permission claim grammar. Nothing presented at callback time can widen the
/// stored scope — the token value carries no claims of its own, and validation
/// projects exactly the stored, frozen grant set.
/// </para>
/// <para>
/// The hydrated principal uses the same role/<c>permission</c>/<c>tenant_id</c>
/// claim grammar the shared RBAC pipeline already enforces, so authorization is
/// never forked: the same service-scoped-role and per-operation permission checks
/// every other request flows through enforce the intersection.
/// </para>
/// </remarks>
internal sealed partial class ScopedJobTokenIssuer(
    IMemoryCache memoryCache,
    ILogger<ScopedJobTokenIssuer> logger,
    IDistributedCache? distributedCache = null) : IScopedJobTokenIssuer
{
    internal const string AuthSchemeName = "ScopedJobToken";
    internal const string AuthTypeClaimValue = "scoped-job-token";
    internal const string TenantClaimType = "tenant_id";
    internal const string JobIdClaimType = "honua_job_id";
    internal const string PermissionClaimType = "permission";
    internal const string RolesClaimType = "roles";
    private const string TokenKeyPrefix = "scoped-job-auth:token:";

    private readonly IMemoryCache _memoryCache = memoryCache;
    private readonly ILogger<ScopedJobTokenIssuer> _logger = logger;
    private readonly IDistributedCache? _distributedCache = distributedCache;

    /// <inheritdoc />
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
        // resource scope. The owner snapshot is the SOLE input — nothing the future
        // job supplies participates here.
        var effectiveScope = ScopedJobAttenuation.Intersect(
            request.Roles,
            ownerGrants: null,
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
            Roles = claims.Roles.ToArray(),
            Permissions = claims.Permissions.ToArray(),
            ExpiresAt = request.ExpiresAt,
        };

        await SetAsync(
            TokenKeyPrefix + token,
            record,
            request.ExpiresAt,
            ScopedJobTokenJsonContext.Default.ScopedJobTokenRecord,
            cancellationToken).ConfigureAwait(false);

        ScopedJobTokenLog.TokenIssued(_logger, record.JobId, claims.Roles.Count, claims.Permissions.Count);
        return new ScopedJobTokenIssuance(token, request.ExpiresAt);
    }

    /// <inheritdoc />
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
        return new ScopedJobTokenValidation(principal, record.JobId, record.ExpiresAt);
    }

    /// <inheritdoc />
    public Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.CompletedTask;
        }

        return RemoveAsync(TokenKeyPrefix + token, cancellationToken);
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

        foreach (var role in record.Roles)
        {
            if (!string.IsNullOrWhiteSpace(role))
            {
                // Project both the canonical Role claim (so IsInRole and the shared
                // role enumeration both see it) and the configurable "roles" claim
                // the RBAC pipeline also reads.
                claims.Add(new Claim(ClaimTypes.Role, role));
                claims.Add(new Claim(RolesClaimType, role));
            }
        }

        foreach (var permission in record.Permissions)
        {
            if (!string.IsNullOrWhiteSpace(permission))
            {
                claims.Add(new Claim(PermissionClaimType, permission));
            }
        }

        var identity = new ClaimsIdentity(
            claims,
            AuthSchemeName,
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    private async Task SetAsync<T>(
        string key,
        T value,
        DateTimeOffset expiresAt,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        if (_distributedCache is null)
        {
            _memoryCache.Set(key, payload, expiresAt);
            return;
        }

        try
        {
            await _distributedCache.SetAsync(
                key,
                payload,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = expiresAt
                },
                cancellationToken).ConfigureAwait(false);
            _memoryCache.Set(key, payload, expiresAt);
        }
        catch (Exception ex)
        {
            _memoryCache.Remove(key);
            ScopedJobTokenLog.DistributedCachePersistFailed(_logger, LogValueRedactor.Hash(key), ex);
            // Surface persistence failures so the caller does not believe a token
            // was issued when another replica will not see it.
            throw;
        }
    }

    private async Task<ScopedJobTokenRecord?> GetAsync(string token, CancellationToken cancellationToken)
    {
        var key = TokenKeyPrefix + token;
        byte[]? payload = null;

        if (_distributedCache is not null)
        {
            try
            {
                payload = await _distributedCache.GetAsync(key, cancellationToken).ConfigureAwait(false);
                if (payload is null)
                {
                    _memoryCache.Remove(key);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _memoryCache.Remove(key);
                ScopedJobTokenLog.DistributedCacheReadFailed(_logger, LogValueRedactor.Hash(key), ex);
                return null;
            }
        }
        else if (!_memoryCache.TryGetValue(key, out payload) || payload is null)
        {
            return null;
        }

        var value = JsonSerializer.Deserialize(payload, ScopedJobTokenJsonContext.Default.ScopedJobTokenRecord);
        if (value is null)
        {
            await RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (value.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return null;
        }

        _memoryCache.Set(key, payload, value.ExpiresAt);
        return value;
    }

    private async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        _memoryCache.Remove(key);

        if (_distributedCache is null)
        {
            return;
        }

        try
        {
            await _distributedCache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ScopedJobTokenLog.DistributedCacheRemoveFailed(_logger, LogValueRedactor.Hash(key), ex);
        }
    }
}

/// <summary>
/// Distributed-cache record backing a scoped-job token. Carries the frozen,
/// already-intersected role/permission grants plus the bound job id; the token
/// value itself is an opaque cache key with no embedded claims.
/// </summary>
internal sealed class ScopedJobTokenRecord
{
    public required string PrincipalId { get; init; }

    public string? TenantId { get; init; }

    public required string JobId { get; init; }

    public required string[] Roles { get; init; }

    public required string[] Permissions { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ScopedJobTokenRecord))]
internal sealed partial class ScopedJobTokenJsonContext : JsonSerializerContext
{
}
