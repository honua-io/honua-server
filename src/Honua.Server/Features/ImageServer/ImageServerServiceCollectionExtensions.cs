// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.ImageServer.Handlers;

namespace Honua.Server.Features.ImageServer;

/// <summary>
/// Service collection extensions for Image Server feature registration.
/// </summary>
internal static class ImageServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers Image Server services with dependency injection.
    /// </summary>
    public static IServiceCollection AddImageServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register handlers
        services.AddScoped<ImageServerMetadataHandler>();
        services.AddScoped<ImageServerExportHandler>();
        services.AddScoped<ImageServerIdentifyHandler>();
        services.AddScoped<ImageServerTileHandler>();

        return services;
    }
}
