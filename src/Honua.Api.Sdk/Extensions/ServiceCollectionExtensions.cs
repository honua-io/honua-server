// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using Honua.Api.Sdk.Clients;
using Honua.Core.Transport.Clients;

namespace Honua.Api.Sdk.Extensions;

/// <summary>
/// Extension methods for registering Honua API client services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Honua API client with the service collection.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configureOptions">Configuration action</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddHonuaApiClient(
        this IServiceCollection services,
        Action<HonuaApiClientOptions> configureOptions)
    {
        return services.AddHonuaApiClient(Options.Create(new HonuaApiClientOptions()), configureOptions);
    }

    /// <summary>
    /// Registers the Honua API client with the service collection using IOptions pattern.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="options">Pre-configured options</param>
    /// <param name="configureOptions">Additional configuration action</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddHonuaApiClient(
        this IServiceCollection services,
        IOptions<HonuaApiClientOptions> options,
        Action<HonuaApiClientOptions>? configureOptions = null)
    {
        // Configure options
        services.Configure<HonuaApiClientOptions>(opts =>
        {
            var sourceOptions = options.Value;
            opts.BaseAddress = sourceOptions.BaseAddress;
            opts.ApiKey = sourceOptions.ApiKey;
            opts.BearerToken = sourceOptions.BearerToken;
            opts.UseConnectionPooling = sourceOptions.UseConnectionPooling;
            opts.MaxConnectionsPerServer = sourceOptions.MaxConnectionsPerServer;
            opts.ConnectionLifetime = sourceOptions.ConnectionLifetime;
            opts.MaxRetryAttempts = sourceOptions.MaxRetryAttempts;
            opts.RetryDelay = sourceOptions.RetryDelay;
            opts.RequestTimeout = sourceOptions.RequestTimeout;
            opts.StreamingPageSize = sourceOptions.StreamingPageSize;
            opts.StreamingTimeout = sourceOptions.StreamingTimeout;
            opts.PreferGrpc = sourceOptions.PreferGrpc;
            opts.CustomHeaders = new Dictionary<string, string>(sourceOptions.CustomHeaders);

            configureOptions?.Invoke(opts);
        });

        services.AddSingleton<GrpcFeatureServiceClient<ServerContext>>(CreateGrpcFeatureClient);

        // Register the main client against the concrete gRPC transport to avoid circular resolution.
        services.AddSingleton<HonuaApiClient>(serviceProvider =>
            new HonuaApiClient(
                serviceProvider.GetRequiredService<GrpcFeatureServiceClient<ServerContext>>(),
                serviceProvider.GetRequiredService<IOptions<HonuaApiClientOptions>>(),
                serviceProvider.GetRequiredService<ILogger<HonuaApiClient>>()));

        // Register the interface binding
        services.AddSingleton<IFeatureServiceClient<ServerContext>>(
            serviceProvider => serviceProvider.GetRequiredService<HonuaApiClient>());

        return services;
    }

    private static GrpcFeatureServiceClient<ServerContext> CreateGrpcFeatureClient(IServiceProvider serviceProvider)
    {
        var clientOptions = serviceProvider.GetRequiredService<IOptions<HonuaApiClientOptions>>().Value;
        var logger = serviceProvider.GetService<ILogger<GrpcFeatureServiceClient<ServerContext>>>();
        var grpcOptions = new GrpcClientOptions
        {
            RequestTimeout = clientOptions.RequestTimeout,
            StreamTimeout = clientOptions.StreamingTimeout
        };

        return new GrpcFeatureServiceClient<ServerContext>(
            context => CreateGrpcFeatureServiceClient(clientOptions, context),
            grpcOptions,
            logger);
    }

    private static Geospatial.V1.FeatureService.FeatureServiceClient CreateGrpcFeatureServiceClient(
        HonuaApiClientOptions clientOptions,
        ServerContext context)
    {
        if (!Uri.TryCreate(clientOptions.BaseAddress, UriKind.Absolute, out var serverAddress))
        {
            throw new InvalidOperationException("Honua API client requires an absolute BaseAddress.");
        }

        var httpClient = CreateHttpClient(serverAddress, clientOptions, context);
        var channel = GrpcChannel.ForAddress(serverAddress, new GrpcChannelOptions { HttpClient = httpClient });
        return new Geospatial.V1.FeatureService.FeatureServiceClient(channel);
    }

    private static HttpClient CreateHttpClient(
        Uri serverAddress,
        HonuaApiClientOptions clientOptions,
        ServerContext context)
    {
        var handler = CreateHttpClientHandler(clientOptions);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = serverAddress,
            Timeout = context.Timeout ?? clientOptions.RequestTimeout
        };

        if (!string.IsNullOrWhiteSpace(clientOptions.ApiKey))
        {
            httpClient.DefaultRequestHeaders.Add("X-API-Key", clientOptions.ApiKey);
        }
        else if (!string.IsNullOrWhiteSpace(clientOptions.BearerToken))
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", clientOptions.BearerToken);
        }

        foreach (var header in clientOptions.CustomHeaders)
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (context.Headers != null)
        {
            foreach (var header in context.Headers)
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return httpClient;
    }

    internal static HttpClientHandler CreateHttpClientHandler(HonuaApiClientOptions clientOptions)
    {
        return new HttpClientHandler
        {
            MaxConnectionsPerServer = clientOptions.MaxConnectionsPerServer
        };
    }
}
