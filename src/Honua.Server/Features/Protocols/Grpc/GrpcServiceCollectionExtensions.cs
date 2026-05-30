// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.GeoServices;
using System.IO.Compression;
using Honua.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Protocols.Grpc;

/// <summary>
/// Configuration options for the Honua gRPC service.
/// </summary>
internal sealed class GrpcOptions
{
    /// <summary>
    /// Number of features per page in streaming responses. Default 1000.
    /// </summary>
    public int StreamBatchSize { get; set; } = 1000;
}

/// <summary>
/// Extension methods for registering gRPC services.
/// </summary>
internal static class GrpcServiceCollectionExtensions
{
    /// <summary>
    /// Registers gRPC infrastructure and the HonuaFeatureService.
    /// </summary>
    public static IServiceCollection AddHonuaGrpc(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        const int defaultMessageSize = 16 * 1024 * 1024;
        var maxReceiveMessageSize = configuration.GetValue<int?>("Grpc:MaxReceiveMessageSize")
            ?? defaultMessageSize;
        var maxSendMessageSize = configuration.GetValue<int?>("Grpc:MaxSendMessageSize")
            ?? defaultMessageSize;
        var responseCompressionAlgorithm = configuration.GetValue<string>("Grpc:ResponseCompressionAlgorithm");
        if (string.IsNullOrWhiteSpace(responseCompressionAlgorithm))
        {
            responseCompressionAlgorithm = "gzip";
        }

        var enableDetailedErrors = configuration.GetValue<bool?>("Grpc:EnableDetailedErrors") ?? false;

        services.AddGrpc(options =>
        {
            options.EnableDetailedErrors = enableDetailedErrors;
            options.MaxReceiveMessageSize = maxReceiveMessageSize;
            options.MaxSendMessageSize = maxSendMessageSize;
            options.Interceptors.Add<GrpcExceptionInterceptor>();

            // Enable wire-level compression negotiation for gRPC calls.
            options.ResponseCompressionAlgorithm = responseCompressionAlgorithm;
            options.ResponseCompressionLevel = CompressionLevel.Fastest;
        });

        services.Configure<GrpcOptions>(options =>
        {
            options.StreamBatchSize = configuration.GetValue<int?>("Grpc:StreamBatchSize") ?? 1000;
        });

        services.TryAddSingleton<GrpcExceptionInterceptor>();
        services.TryAddScoped<SpatialReferenceResolver>();

        services.AddGrpcHealthChecks();
        services.AddGrpcReflection();

        return services;
    }
}
