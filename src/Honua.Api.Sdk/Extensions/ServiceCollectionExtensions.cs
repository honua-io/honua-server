// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;
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

        // Register gRPC client with connection pooling and resilience
        services.AddGrpcClient<IFeatureServiceClient<ServerContext>>(
            (serviceProvider, client) =>
            {
                var clientOptions = serviceProvider.GetRequiredService<IOptions<HonuaApiClientOptions>>().Value;
                client.Address = new Uri(clientOptions.BaseAddress);

                // Configure authentication headers
                if (!string.IsNullOrEmpty(clientOptions.ApiKey))
                {
                    client.ChannelOptions.HttpClient?.DefaultRequestHeaders.Add("X-API-Key", clientOptions.ApiKey);
                }
                else if (!string.IsNullOrEmpty(clientOptions.BearerToken))
                {
                    client.ChannelOptions.HttpClient?.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", clientOptions.BearerToken);
                }

                // Add custom headers
                foreach (var header in clientOptions.CustomHeaders)
                {
                    client.ChannelOptions.HttpClient?.DefaultRequestHeaders.Add(header.Key, header.Value);
                }
            })
            .ConfigureChannel((serviceProvider, options) =>
            {
                var clientOptions = serviceProvider.GetRequiredService<IOptions<HonuaApiClientOptions>>().Value;

                if (clientOptions.UseConnectionPooling)
                {
                    options.HttpHandler = CreatePooledHttpHandler(clientOptions);
                }

                options.MaxReceiveMessageSize = 64 * 1024 * 1024; // 64MB for large feature sets
                options.MaxSendMessageSize = 16 * 1024 * 1024;    // 16MB for bulk edits
            })
            .AddPolicyHandler((serviceProvider, request) =>
            {
                var clientOptions = serviceProvider.GetRequiredService<IOptions<HonuaApiClientOptions>>().Value;
                return CreateRetryPolicy(clientOptions);
            })
            .AddPolicyHandler((serviceProvider, request) =>
            {
                var clientOptions = serviceProvider.GetRequiredService<IOptions<HonuaApiClientOptions>>().Value;
                return CreateCircuitBreakerPolicy(clientOptions);
            });

        // Register the main client
        services.AddSingleton<HonuaApiClient>();

        // Register the interface binding
        services.AddSingleton<IFeatureServiceClient<ServerContext>>(
            serviceProvider => serviceProvider.GetRequiredService<HonuaApiClient>());

        return services;
    }

    private static HttpClientHandler CreatePooledHttpHandler(HonuaApiClientOptions options)
    {
        return new HttpClientHandler
        {
            MaxConnectionsPerServer = options.MaxConnectionsPerServer,
            // Enable HTTP/2 for better gRPC performance
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
            {
                // In production, implement proper certificate validation
                return true;
            }
        };
    }

    private static IAsyncPolicy<HttpResponseMessage> CreateRetryPolicy(HonuaApiClientOptions options)
    {
        var retryStatusCodes = new[]
        {
            HttpStatusCode.RequestTimeout,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.BadGateway,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.GatewayTimeout
        };

        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => retryStatusCodes.Contains(msg.StatusCode))
            .WaitAndRetryAsync(
                retryCount: options.MaxRetryAttempts,
                sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(
                    Math.Pow(2, retryAttempt) * options.RetryDelay.TotalMilliseconds));
    }

    private static IAsyncPolicy<HttpResponseMessage> CreateCircuitBreakerPolicy(HonuaApiClientOptions options)
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (exception, duration) =>
                {
                    // Log circuit breaker activation
                },
                onReset: () =>
                {
                    // Log circuit breaker reset
                });
    }
}

/// <summary>
/// Extension methods for logging contexts.
/// </summary>
internal static class ContextExtensions
{
    public static Microsoft.Extensions.Logging.ILogger? GetLogger(this Polly.Context context)
    {
        context.TryGetValue("logger", out var logger);
        return logger as Microsoft.Extensions.Logging.ILogger;
    }
}