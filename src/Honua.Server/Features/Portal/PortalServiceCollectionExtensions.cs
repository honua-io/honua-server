// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Portal.Sharing;
using Npgsql;

namespace Honua.Server.Features.Portal;

internal static class PortalServiceCollectionExtensions
{
    internal static IServiceCollection AddPostgresPortalSharing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IPortalGroupStore>(sp =>
            new PostgresPortalGroupStore(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                configuration["Database:Schema"]));
        services.AddSingleton<IPortalItemSharingStore>(sp =>
            new PostgresPortalItemSharingStore(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<IHttpContextAccessor>(),
                configuration["Database:Schema"]));
        services.AddSingleton<IPortalAudienceSnapshotProvider>(sp =>
            new PostgresPortalAudienceSnapshotProvider(
                sp.GetRequiredService<NpgsqlDataSource>(),
                configuration["Database:Schema"]));
        return services;
    }
}
