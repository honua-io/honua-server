// Behavior reference: registration ordering bug #1575 — the durable PostgresRoleStore
// (registered in AddPostgreSqlServices via RegisterInfrastructureServices, earlier in
// Program.cs) was shadowed by a later unconditional AddSingleton<IRoleStore, InMemory...>.
// Using TryAdd makes the in-memory store a true default that durable providers override.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Server.Features.Admin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Startup;

/// <summary>
/// Registers the in-memory control-plane IAM stores as defaults. A durable provider
/// implementation registered earlier (e.g. <c>PostgresRoleStore</c>) is preserved, because
/// these use <c>TryAdd</c> rather than an unconditional <c>Add</c> (see #1575).
/// </summary>
internal static class ControlPlaneIamDefaults
{
    /// <summary>
    /// Adds in-memory <see cref="IOidcProviderStore"/>, <see cref="IUserStore"/>, and
    /// <see cref="IRoleStore"/> defaults using <c>TryAdd</c> so durable provider
    /// implementations registered earlier take precedence.
    /// </summary>
    public static IServiceCollection AddInMemoryControlPlaneIamDefaults(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IOidcProviderStore, InMemoryOidcProviderStore>();
        services.TryAddSingleton<IUserStore, InMemoryUserStore>();
        services.TryAddSingleton<IRoleStore, InMemoryRoleStore>();

        return services;
    }
}
