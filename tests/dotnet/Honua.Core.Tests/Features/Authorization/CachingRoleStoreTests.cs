// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Tests.Features.Authorization;

/// <summary>
/// Unit tests for <see cref="RolePermissionCache"/> and the
/// <see cref="CachingRoleStore"/> decorator (#1593): key normalization,
/// TTL expiry, bounded size, mutation-driven invalidation, and unchanged
/// <see cref="IRoleStore"/> read/write semantics.
/// </summary>
public sealed class CachingRoleStoreTests
{
    private static PermissionGrant Grant(string service, string layer, string operation)
        => new() { Service = service, Layer = layer, Operation = operation };

    private static RoleDefinition Role(string name, params PermissionGrant[] grants)
        => new() { Name = name, Permissions = grants };

    // ---------------------------------------------------------------------
    // RolePermissionCache key normalization
    // ---------------------------------------------------------------------

    [Fact]
    public void CreateKey_NormalizesCaseWhitespaceOrderAndDuplicates()
    {
        var a = RolePermissionCache.CreateKey(["Editor", "  viewer ", "EDITOR"]);
        var b = RolePermissionCache.CreateKey(["viewer", "editor"]);

        a.Should().Be(b);
        a.Should().Be("editor\nviewer");
    }

    [Fact]
    public void CreateKey_EmptyOrWhitespaceOnlyRoles_ReturnsEmpty()
    {
        RolePermissionCache.CreateKey([]).Should().BeEmpty();
        RolePermissionCache.CreateKey(["", "   "]).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------
    // RolePermissionCache TTL + bounding
    // ---------------------------------------------------------------------

    [Fact]
    public void TryGet_AfterTtlElapses_ReportsMissAndEvicts()
    {
        var time = new MutableTimeProvider();
        var cache = new RolePermissionCache(TimeSpan.FromSeconds(30), timeProvider: time);
        cache.Set("key", [Grant("svc", "*", "read")]);

        cache.TryGet("key", out _).Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(31));

        cache.TryGet("key", out _).Should().BeFalse();
        cache.Count.Should().Be(0, "expired entries are removed lazily on access");
    }

    [Fact]
    public void Set_BeyondMaxEntries_StaysBounded()
    {
        var cache = new RolePermissionCache(TimeSpan.FromSeconds(30), maxEntries: 2);

        cache.Set("a", []);
        cache.Set("b", []);
        cache.Set("c", []);

        cache.Count.Should().BeLessThanOrEqualTo(2);
        cache.TryGet("c", out _).Should().BeTrue("the newest entry is always admitted");
    }

    [Fact]
    public void Set_AtCapacityWithExpiredEntries_SweepsInsteadOfClearing()
    {
        var time = new MutableTimeProvider();
        var cache = new RolePermissionCache(TimeSpan.FromSeconds(30), maxEntries: 2, timeProvider: time);

        cache.Set("stale", []);
        time.Advance(TimeSpan.FromSeconds(31));
        cache.Set("fresh-1", []);
        cache.Set("fresh-2", []);

        cache.TryGet("fresh-1", out _).Should().BeTrue("only expired entries should have been swept");
        cache.TryGet("fresh-2", out _).Should().BeTrue();
    }

    [Fact]
    public void InvalidateAll_DropsEveryEntry()
    {
        var cache = new RolePermissionCache();
        cache.Set("a", [Grant("svc", "*", "read")]);

        cache.InvalidateAll();

        cache.Count.Should().Be(0);
        cache.TryGet("a", out _).Should().BeFalse();
    }

    [Fact]
    public void Constructor_NonPositiveTtlOrMaxEntries_Throws()
    {
        var negativeTtl = () => new RolePermissionCache(TimeSpan.Zero);
        var negativeMax = () => new RolePermissionCache(maxEntries: 0);

        negativeTtl.Should().Throw<ArgumentOutOfRangeException>();
        negativeMax.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---------------------------------------------------------------------
    // CachingRoleStore: hot-path caching
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetEffectivePermissions_SecondCallWithinTtl_ServedFromCache()
    {
        var inner = new CountingRoleStore();
        var store = new CachingRoleStore(inner, new RolePermissionCache());
        await inner.CreateRoleAsync(Role("editor", Grant("svc", "*", "write")));

        var first = await store.GetEffectivePermissionsAsync("user-1", ["editor"]);
        var second = await store.GetEffectivePermissionsAsync("user-1", ["editor"]);

        inner.EffectivePermissionCalls.Should().Be(1);
        second.Permissions.Should().BeEquivalentTo(first.Permissions);
    }

    [Fact]
    public async Task GetEffectivePermissions_RoleOrderAndCasing_ShareOneCacheEntry()
    {
        var inner = new CountingRoleStore();
        var store = new CachingRoleStore(inner, new RolePermissionCache());

        await store.GetEffectivePermissionsAsync("user-1", ["Editor", "viewer"]);
        await store.GetEffectivePermissionsAsync("user-2", ["VIEWER", "editor "]);

        inner.EffectivePermissionCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetEffectivePermissions_CacheHit_EchoesCallerIdentityNotCachedOne()
    {
        var inner = new CountingRoleStore();
        var store = new CachingRoleStore(inner, new RolePermissionCache());
        await inner.CreateRoleAsync(Role("editor", Grant("svc", "*", "write")));

        await store.GetEffectivePermissionsAsync("user-1", ["editor"]);
        var hit = await store.GetEffectivePermissionsAsync("user-2", ["editor"]);

        hit.UserId.Should().Be("user-2", "cached grants must never be attributed to a different principal");
        hit.Roles.Should().Equal("editor");
        hit.Permissions.Should().ContainSingle(g => g.Operation == "write");
    }

    [Fact]
    public async Task GetEffectivePermissions_EmptyOrWhitespaceRoles_BypassesCache()
    {
        var inner = new CountingRoleStore();
        var store = new CachingRoleStore(inner, new RolePermissionCache());

        await store.GetEffectivePermissionsAsync("user-1", []);
        await store.GetEffectivePermissionsAsync("user-1", ["   "]);
        await store.GetEffectivePermissionsAsync("user-1", []);

        inner.EffectivePermissionCalls.Should().Be(3, "empty role sets are not cached");
    }

    [Fact]
    public async Task GetEffectivePermissions_AfterTtl_RefetchesFromInner()
    {
        var time = new MutableTimeProvider();
        var inner = new CountingRoleStore();
        var store = new CachingRoleStore(inner, new RolePermissionCache(TimeSpan.FromSeconds(30), timeProvider: time));

        await store.GetEffectivePermissionsAsync("user-1", ["editor"]);
        time.Advance(TimeSpan.FromSeconds(31));
        await store.GetEffectivePermissionsAsync("user-1", ["editor"]);

        inner.EffectivePermissionCalls.Should().Be(2);
    }

    // ---------------------------------------------------------------------
    // CachingRoleStore: invalidation on mutation paths
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CreateRole_InvalidatesCachedPermissions()
    {
        var inner = new CountingRoleStore();
        var store = new CachingRoleStore(inner, new RolePermissionCache());

        var stale = await store.GetEffectivePermissionsAsync("user-1", ["editor"]);
        stale.Permissions.Should().BeEmpty();

        await store.CreateRoleAsync(Role("editor", Grant("svc", "*", "write")));

        var fresh = await store.GetEffectivePermissionsAsync("user-1", ["editor"]);
        fresh.Permissions.Should().ContainSingle(g => g.Operation == "write");
        inner.EffectivePermissionCalls.Should().Be(2);
    }

    [Fact]
    public async Task UpdateRole_InvalidatesCachedPermissions()
    {
        var inner = new CountingRoleStore();
        var store = new CachingRoleStore(inner, new RolePermissionCache());
        var role = await store.CreateRoleAsync(Role("editor", Grant("svc", "*", "read")));

        await store.GetEffectivePermissionsAsync("user-1", ["editor"]);
        await store.UpdateRoleAsync(new RoleDefinition
        {
            RoleId = role.RoleId,
            Name = role.Name,
            Permissions = [Grant("svc", "*", "write")],
        });

        var fresh = await store.GetEffectivePermissionsAsync("user-1", ["editor"]);
        fresh.Permissions.Should().ContainSingle(g => g.Operation == "write");
    }

    [Fact]
    public async Task DeleteRole_InvalidatesCachedPermissions()
    {
        var inner = new CountingRoleStore();
        var store = new CachingRoleStore(inner, new RolePermissionCache());
        var role = await store.CreateRoleAsync(Role("editor", Grant("svc", "*", "write")));

        (await store.GetEffectivePermissionsAsync("user-1", ["editor"])).Permissions.Should().NotBeEmpty();

        await store.DeleteRoleAsync(role.RoleId);

        (await store.GetEffectivePermissionsAsync("user-1", ["editor"])).Permissions.Should().BeEmpty();
    }

    [Fact]
    public async Task SetPermissions_InvalidatesCachedPermissions()
    {
        var inner = new CountingRoleStore();
        var store = new CachingRoleStore(inner, new RolePermissionCache());
        var role = await store.CreateRoleAsync(Role("editor", Grant("svc", "*", "read")));

        await store.GetEffectivePermissionsAsync("user-1", ["editor"]);
        await store.SetPermissionsAsync(role.RoleId, [Grant("svc", "layer-1", "delete")]);

        var fresh = await store.GetEffectivePermissionsAsync("user-1", ["editor"]);
        fresh.Permissions.Should().ContainSingle(g => g.Operation == "delete" && g.Layer == "layer-1");
    }

    // ---------------------------------------------------------------------
    // CachingRoleStore: pass-through semantics
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AdministrativeReads_PassThroughUncached()
    {
        var inner = new CountingRoleStore();
        var store = new CachingRoleStore(inner, new RolePermissionCache());
        var role = await store.CreateRoleAsync(Role("editor", Grant("svc", "*", "read")));

        (await store.ListRolesAsync()).Should().ContainSingle(r => r.RoleId == role.RoleId);
        (await store.GetRoleAsync(role.RoleId))!.Name.Should().Be("editor");
        (await store.GetPermissionsAsync(role.RoleId)).Should().ContainSingle();
        (await store.GetRoleAsync(Guid.NewGuid())).Should().BeNull();
    }

    /// <summary>
    /// Minimal in-memory <see cref="IRoleStore"/> mirroring the production
    /// in-memory store's contract, instrumented to count
    /// <see cref="GetEffectivePermissionsAsync"/> calls.
    /// </summary>
    private sealed class CountingRoleStore : IRoleStore
    {
        private readonly Dictionary<Guid, RoleDefinition> _roles = new();

        public int EffectivePermissionCalls { get; private set; }

        public Task<IReadOnlyList<RoleDefinition>> ListRolesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RoleDefinition>>([.. _roles.Values]);

        public Task<RoleDefinition?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult(_roles.GetValueOrDefault(roleId));

        public Task<RoleDefinition> CreateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
        {
            _roles[role.RoleId] = role;
            return Task.FromResult(role);
        }

        public Task<RoleDefinition?> UpdateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
        {
            if (!_roles.ContainsKey(role.RoleId))
            {
                return Task.FromResult<RoleDefinition?>(null);
            }

            _roles[role.RoleId] = role;
            return Task.FromResult<RoleDefinition?>(role);
        }

        public Task<bool> DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult(_roles.Remove(roleId));

        public Task<IReadOnlyList<PermissionGrant>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PermissionGrant>>(_roles.TryGetValue(roleId, out var role) ? role.Permissions : []);

        public Task<IReadOnlyList<PermissionGrant>> SetPermissionsAsync(
            Guid roleId,
            IReadOnlyList<PermissionGrant> permissions,
            CancellationToken cancellationToken = default)
        {
            if (!_roles.TryGetValue(roleId, out var role))
            {
                return Task.FromResult<IReadOnlyList<PermissionGrant>>([]);
            }

            _roles[roleId] = new RoleDefinition
            {
                RoleId = role.RoleId,
                Name = role.Name,
                Description = role.Description,
                IsBuiltIn = role.IsBuiltIn,
                Permissions = permissions,
                CreatedAt = role.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            return Task.FromResult(permissions);
        }

        public Task<EffectivePermissions> GetEffectivePermissionsAsync(
            string userId,
            IReadOnlyList<string> roles,
            CancellationToken cancellationToken = default)
        {
            EffectivePermissionCalls++;

            var lowered = roles
                .Where(static r => !string.IsNullOrWhiteSpace(r))
                .Select(static r => r.Trim().ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);

            var grants = _roles.Values
                .Where(r => lowered.Contains(r.Name.ToLowerInvariant()))
                .SelectMany(static r => r.Permissions)
                .ToList();

            return Task.FromResult(new EffectivePermissions
            {
                UserId = userId ?? string.Empty,
                Roles = roles,
                Permissions = grants,
            });
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan delta) => _utcNow += delta;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
