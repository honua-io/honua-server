// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed class GrpcOptions
{
    public int StreamBatchSize { get; set; } = 1000;
}

internal static class GrpcServiceCollectionExtensions
{
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
        services.TryAddSingleton<GrpcApplyEditsIdempotencyStore>(sp =>
            new GrpcApplyEditsIdempotencyStore(
                sp.GetService<StackExchange.Redis.IConnectionMultiplexer>(),
                sp.GetService<ILogger<GrpcApplyEditsIdempotencyStore>>(),
                sp.GetService<NpgsqlDataSource>() is { } dataSource
                    ? new PostgresFeatureEditIdempotencyReceipts(
                        dataSource,
                        sp.GetService<PostgresSchemaConfiguration>()?.MetadataSchema)
                    : null));
#if !HONUA_EXCLUDE_GEOSERVICES
        services.TryAddScoped<SpatialReferenceResolver>();
#endif

        services.AddGrpcHealthChecks();
        services.AddGrpcReflection();

        return services;
    }
}
