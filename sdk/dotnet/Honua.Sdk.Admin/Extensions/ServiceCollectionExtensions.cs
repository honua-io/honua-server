// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Sdk.Admin.Extensions;

/// <summary>
/// Extension methods for registering the Honua Admin client with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Honua Admin client and related services with the DI container.
    /// </summary>
    /// <param name="services">The service collection to register with.</param>
    /// <param name="configure">Configuration delegate for client options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHonuaAdmin(
        this IServiceCollection services,
        Action<HonuaAdminClientOptions> configure)
    {
        services.Configure(configure);
        services.AddTransient<HonuaAdminAuthHandler>();
        services.AddHttpClient<IHonuaAdminClient, HonuaAdminClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<HonuaAdminClientOptions>>().Value;
            client.BaseAddress = options.BaseAddress;
        })
        .AddHttpMessageHandler<HonuaAdminAuthHandler>();
        return services;
    }
}
