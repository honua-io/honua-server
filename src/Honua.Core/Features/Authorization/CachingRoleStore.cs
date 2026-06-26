// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Features.Authorization;

/// <summary>
/// Read-caching <see cref="IRoleStore"/> decorator (#1593). Serves the hot-path
/// <see cref="GetEffectivePermissionsAsync"/> lookup — invoked by
/// <see cref="PermissionResolver.AuthorizeAsync"/> on every RBAC-enforced
/// request — from a shared singleton <see cref="RolePermissionCache"/> instead
/// of hitting the underlying (typically database-backed, per-request scoped)
/// store each time.
/// </summary>
/// <remarks>
/// <para>
/// Semantics of the wrapped <see cref="IRoleStore"/> are unchanged: all
/// administrative reads (<see cref="ListRolesAsync"/>, <see cref="GetRoleAsync"/>,
/// <see cref="GetPermissionsAsync"/>) pass through uncached, and every mutation
/// (<see cref="CreateRoleAsync"/>, <see cref="UpdateRoleAsync"/>,
/// <see cref="DeleteRoleAsync"/>, <see cref="SetPermissionsAsync"/>) delegates
/// to the inner store and then invalidates the cache, so same-node changes are
/// visible on the next authorization check. On multi-node deployments a
/// mutation performed on another node becomes visible within the cache TTL
/// (<see cref="RolePermissionCache.Ttl"/>, default 30 seconds) — brief, bounded
/// staleness that authorization data tolerates by design.
/// </para>
/// <para>
/// Cache entries are keyed by the normalized role-name set only; the caller's
/// user id and role list are echoed back verbatim on a hit, so cached grants are
/// never attributed to a different principal.
/// </para>
/// </remarks>
public sealed partial class CachingRoleStore : IRoleStore
{
    private readonly IRoleStore _inner;
    private readonly RolePermissionCache _cache;
    private readonly ILogger<CachingRoleStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingRoleStore"/> class.
    /// </summary>
    /// <param name="inner">The authoritative role store to decorate.</param>
    /// <param name="cache">The shared (singleton) role-permission cache.</param>
    /// <param name="logger">Optional logger.</param>
    public CachingRoleStore(IRoleStore inner, RolePermissionCache cache, ILogger<CachingRoleStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);
        _inner = inner;
        _cache = cache;
        _logger = logger ?? NullLogger<CachingRoleStore>.Instance;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RoleDefinition>> ListRolesAsync(CancellationToken cancellationToken = default)
        => _inner.ListRolesAsync(cancellationToken);

    /// <inheritdoc />
    public Task<RoleDefinition?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
        => _inner.GetRoleAsync(roleId, cancellationToken);

    /// <inheritdoc />
    public async Task<RoleDefinition> CreateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
    {
        var created = await _inner.CreateRoleAsync(role, cancellationToken).ConfigureAwait(false);
        Invalidate(nameof(CreateRoleAsync));
        return created;
    }

    /// <inheritdoc />
    public async Task<RoleDefinition?> UpdateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
    {
        var updated = await _inner.UpdateRoleAsync(role, cancellationToken).ConfigureAwait(false);
        Invalidate(nameof(UpdateRoleAsync));
        return updated;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var deleted = await _inner.DeleteRoleAsync(roleId, cancellationToken).ConfigureAwait(false);
        Invalidate(nameof(DeleteRoleAsync));
        return deleted;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PermissionGrant>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken = default)
        => _inner.GetPermissionsAsync(roleId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermissionGrant>> SetPermissionsAsync(
        Guid roleId,
        IReadOnlyList<PermissionGrant> permissions,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.SetPermissionsAsync(roleId, permissions, cancellationToken).ConfigureAwait(false);
        Invalidate(nameof(SetPermissionsAsync));
        return result;
    }

    /// <inheritdoc />
    public async Task<EffectivePermissions> GetEffectivePermissionsAsync(
        string userId,
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var key = RolePermissionCache.CreateKey(roles);
        if (key.Length == 0)
        {
            // No usable role names: the inner store short-circuits this cheaply
            // and the result is not worth a cache slot.
            return await _inner.GetEffectivePermissionsAsync(userId, roles, cancellationToken).ConfigureAwait(false);
        }

        if (_cache.TryGet(key, out var cached))
        {
            return new EffectivePermissions
            {
                UserId = userId ?? string.Empty,
                Roles = roles,
                Permissions = cached,
            };
        }

        var resolved = await _inner.GetEffectivePermissionsAsync(userId, roles, cancellationToken).ConfigureAwait(false);
        _cache.Set(key, resolved.Permissions);
        return resolved;
    }

    private void Invalidate(string operation)
    {
        _cache.InvalidateAll();
        Log.CacheInvalidated(_logger, operation);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 7730, Level = LogLevel.Debug, Message = "Role-permission cache invalidated after {Operation}")]
        public static partial void CacheInvalidated(ILogger logger, string operation);
    }
}
