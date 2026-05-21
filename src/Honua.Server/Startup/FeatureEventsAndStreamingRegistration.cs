// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Events.Outbox;
using Honua.Server.Features.Infrastructure.Extensions;
using Honua.Server.Features.Streaming;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Startup;

/// <summary>
/// Registers feature-change event plumbing (event store, retry queue, webhook dispatcher,
/// transactional outbox, stream session manager, and the streaming publisher). The boolean
/// flag forwards the host-level decision about whether in-memory event stores are tolerated
/// (Development / Test) or strictly forbidden (Production / Staging).
/// </summary>
internal static class FeatureEventsAndStreamingRegistration
{
    public static IServiceCollection AddHonuaFeatureEventsAndStreaming(
        this IServiceCollection services,
        IConfiguration configuration,
        bool requiresDurableDistributedEvents)
    {
        services.Configure<FeatureChangeEventOptions>(
            configuration.GetSection(FeatureChangeEventOptions.SectionName));
        services.AddSingleton<IValidateOptions<FeatureChangeWebhookOptions>, FeatureChangeWebhookOptionsValidator>();
        services.AddOptions<FeatureChangeWebhookOptions>()
            .Bind(configuration.GetSection(FeatureChangeWebhookOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IFeatureChangeEventStore>(sp =>
        {
            return new InMemoryFeatureChangeEventStore(
                sp.GetRequiredService<IOptions<FeatureChangeEventOptions>>(),
                sp.GetService<IConnectionMultiplexer>(),
                allowInMemoryFallback: !requiresDurableDistributedEvents);
        });
        services.AddSingleton<IFeatureChangeEventStoreHealth>(sp =>
            (IFeatureChangeEventStoreHealth)sp.GetRequiredService<IFeatureChangeEventStore>());

        // Feature-stream session manager and streaming publisher (#501)
        services.AddOptions<FeatureStreamOptions>()
            .Bind(configuration.GetSection(FeatureStreamOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FeatureStreamOptions>, FeatureStreamOptionsValidator>();
        services.AddSingleton<FeatureStreamSessionManager>(sp =>
            new FeatureStreamSessionManager(
                sp.GetRequiredService<IOptions<FeatureStreamOptions>>(),
                sp.GetRequiredService<ILogger<FeatureStreamSessionManager>>(),
                sp.GetService<IConnectionMultiplexer>()));
        services.AddSingleton(System.Threading.Channels.Channel.CreateUnbounded<PendingFeatureChangeSignal>());
        services.AddSingleton<IFeatureChangeRetryQueue>(sp =>
            new FeatureChangeRetryQueue(
                sp.GetService<IDistributedCache>(),
                sp.GetRequiredService<System.Threading.Channels.Channel<PendingFeatureChangeSignal>>(),
                sp.GetRequiredService<IFeatureChangeEventStore>(),
                sp.GetRequiredService<FeatureStreamSessionManager>(),
                sp.GetRequiredService<ILogger<FeatureChangeRetryQueue>>(),
                sp.GetService<IConnectionMultiplexer>()));
        services.AddSingleton<IFeatureChangeEventPublisher>(sp =>
            new FeatureStreamPublisher(
                sp.GetRequiredService<IFeatureChangeEventStore>(),
                sp.GetRequiredService<FeatureStreamSessionManager>(),
                sp.GetRequiredService<ILogger<FeatureStreamPublisher>>(),
                sp.GetRequiredService<IFeatureChangeRetryQueue>()));
        services.AddSingleton<FeatureMutationEventService>();

        // Feature-change transactional outbox (#692). Capability provider, dispatcher, health,
        // and metrics. The outbox repository itself is registered by the active provider's
        // service-collection extension (Postgres registers a working repository; SQL Server and
        // DuckDB register only the capability provider since they do not support feature writes).
        services.AddSingleton<
            IValidateOptions<OutboxDispatcherOptions>,
            OutboxDispatcherOptionsValidator>();
        services.AddOptions<OutboxDispatcherOptions>()
            .Bind(configuration.GetSection(OutboxDispatcherOptions.SectionName))
            .ValidateOnStart();
        // Default capability provider for hosts that bypass RegisterInfrastructureServices (test
        // factories). The active provider's extension uses AddSingleton, so when it runs first
        // it wins over this TryAdd; when infrastructure registration is skipped, the dispatcher
        // still constructs and immediately exits because SupportsTransactionalOutbox is false.
        services.TryAddSingleton<IOutboxCapabilityProvider, NoOpOutboxCapabilityProvider>();
        services.AddSingleton<OutboxDispatcherBackgroundService>();
        services.AddSingleton<IOutboxHealth>(sp =>
            sp.GetRequiredService<OutboxDispatcherBackgroundService>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<OutboxDispatcherBackgroundService>());
        services.AddHostedService<FeatureChangeRetryBackgroundService>();
        services.AddScoped<FeatureStreamDependencies>();
        services.AddHostedService<FeatureStreamHeartbeatService>();
        services.AddResilientHttpClient(
            "feature-change-webhook",
            "feature-change-webhook",
            HttpResiliencePolicies.FastApiDefaults,
            configureHandler: static () => WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler());
        services.AddHostedService(sp =>
            new FeatureChangeWebhookDispatcher(
                sp.GetRequiredService<IFeatureChangeEventStore>(),
                sp.GetService<IDistributedCache>(),
                sp.GetService<IConnectionMultiplexer>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IOptions<FeatureChangeWebhookOptions>>(),
                sp.GetRequiredService<ILogger<FeatureChangeWebhookDispatcher>>()));

        return services;
    }
}
