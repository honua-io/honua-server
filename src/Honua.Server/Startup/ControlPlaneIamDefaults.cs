// Behavior reference: registration ordering bug #1575 — the durable PostgresRoleStore
// (registered in AddPostgreSqlServices via RegisterInfrastructureServices, earlier in
// Program.cs) was shadowed by a later unconditional AddSingleton<IRoleStore, InMemory...>.
// Using TryAdd makes the in-memory store a true default that durable providers override.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Server.Features.Admin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Server.Startup;

/// <summary>
/// Registers the control-plane IAM stores. When Redis is configured the managed-user and SCIM
/// group stores are Redis-backed so identity state is durable and shared across replicas;
/// otherwise node-local in-memory defaults apply. A durable provider implementation registered
/// earlier (e.g. <c>PostgresRoleStore</c>) is preserved, because these use <c>TryAdd</c>
/// rather than an unconditional <c>Add</c> (see #1575).
/// </summary>
internal static class ControlPlaneIamDefaults
{
    /// <summary>
    /// Adds <see cref="IOidcProviderStore"/>, <see cref="IUserStore"/>, and
    /// <see cref="IRoleStore"/> defaults using <c>TryAdd</c> so durable provider
    /// implementations registered earlier take precedence.
    /// </summary>
    public static IServiceCollection AddInMemoryControlPlaneIamDefaults(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IOidcProviderStore, InMemoryOidcProviderStore>();

        // The user store backs the admin IUserStore surface, the SCIM provisioning surface
        // (IScimUserStore, #510), and deferred-lane membership revalidation (#3081). Deferred
        // workflow firings and approval resumes exist only when Redis is configured (the
        // durable workflow stores require it), and they may run on a different replica — or
        // after a restart — than the one that handled SCIM provisioning. So whenever Redis is
        // present the user/group stores MUST be Redis-backed: a node-local in-memory store
        // would answer membership from a replica that never saw the deprovisioning, silently
        // downgrading revocation to snapshot fallback (honua-server#3081 review).
        if (services.Any(static d => d.ServiceType == typeof(IConnectionMultiplexer)))
        {
            services.TryAddSingleton<RedisUserStore>(static sp =>
                new RedisUserStore(sp.GetRequiredService<IConnectionMultiplexer>()));
            services.TryAddSingleton<IUserStore>(static sp => sp.GetRequiredService<RedisUserStore>());
            services.TryAddSingleton<IScimUserStore>(static sp => sp.GetRequiredService<RedisUserStore>());
            services.TryAddSingleton<IScimGroupStore>(static sp =>
                new RedisScimGroupStore(
                    sp.GetRequiredService<RedisUserStore>(),
                    sp.GetRequiredService<IConnectionMultiplexer>()));
        }

        // In-memory defaults for single-node/no-Redis profiles. Register the concrete
        // singleton once and project all three contracts onto the SAME instance so
        // SCIM-provisioned users are visible to the admin endpoints and group->role sync
        // mutates a single record set.
        services.TryAddSingleton<InMemoryUserStore>();
        services.TryAddSingleton<IUserStore>(static sp => sp.GetRequiredService<InMemoryUserStore>());
        services.TryAddSingleton<IScimUserStore>(static sp => sp.GetRequiredService<InMemoryUserStore>());
        services.TryAddSingleton<IScimGroupStore>(static sp =>
            new InMemoryScimGroupStore(sp.GetRequiredService<InMemoryUserStore>()));

        // Deferred workflow and approval lanes use this provider-overridable seam to replace
        // the role claims captured at publication/submission with the managed identity's
        // CURRENT roles. Providers registered earlier win; identities not mirrored into the
        // managed-user store explicitly fall back to their documented durable snapshot.
        services.TryAddSingleton<IPrincipalMembershipSource, ManagedUserPrincipalMembershipSource>();

        services.TryAddSingleton<IRoleStore, InMemoryRoleStore>();

        return services;
    }
}
