// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Grpc;

/// <summary>
/// Extension methods for registering gRPC services.
/// </summary>
internal static class GrpcServiceCollectionExtensions
{
    /// <summary>
    /// Registers gRPC infrastructure and the HonuaFeatureService.
    /// </summary>
    public static IServiceCollection AddHonuaGrpc(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddGrpc();
        return services;
    }
}
