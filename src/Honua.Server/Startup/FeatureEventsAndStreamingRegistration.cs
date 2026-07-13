// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Events.Outbox;
using Honua.Infrastructure.Extensions;
using Honua.Server.Features.Streaming;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Startup;

/// <summary>
/// Registers feature-change event plumbing (event store, retry queue, webhook dispatcher,
/// transactional outbox, stream session manager, and the streaming publisher). The boolean
/// flag forwards the host-level decision about whether silent in-memory fallback is tolerated
/// (Development / Test) or strictly forbidden (Production / Staging). The strict durability
/// requirement only applies when Redis is actually configured: with no Redis at all the
/// deployment is a supported single-node/serverless topology (#1618), so the event store runs
/// in explicit in-memory single-node mode (with a startup warning) instead of reporting
/// permanently unhealthy — unconfigured is not unhealthy; unreachable is.
/// </summary>
internal static partial class FeatureEventsAndStreamingRegistration
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
            var redis = sp.GetService<IConnectionMultiplexer>();

            // Durability can only be enforced against a Redis that is actually configured.
            // No Redis registered => single-node/serverless deployment: degrade to explicit
            // node-local in-memory storage (ADR-0017 fallback pattern) instead of reporting
            // the store permanently unhealthy (#1618). When Redis IS configured, the strict
            // environments keep requiring it, so a real Redis outage still fails readiness.
            var allowInMemoryFallback = !requiresDurableDistributedEvents || redis is null;
            if (requiresDurableDistributedEvents && redis is null)
            {
                LogSingleNodeInMemoryEventStore(
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(FeatureEventsAndStreamingRegistration).FullName!));
            }

            return new InMemoryFeatureChangeEventStore(
                sp.GetRequiredService<IOptions<FeatureChangeEventOptions>>(),
                redis,
                allowInMemoryFallback);
        });
        services.AddSingleton<IFeatureChangeEventStoreHealth>(sp =>
            (IFeatureChangeEventStoreHealth)sp.GetRequiredService<IFeatureChangeEventStore>());

        // Feature-stream session manager and streaming publisher (#501)
        services.AddOptions<FeatureStreamOptions>()
            .Bind(configuration.GetSection(FeatureStreamOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FeatureStreamOptions>, FeatureStreamOptionsValidator>();
        services.AddSingleton<FeatureStreamMetrics>();
        services.AddSingleton<FeatureStreamSessionManager>(sp =>
            new FeatureStreamSessionManager(
                sp.GetRequiredService<IOptions<FeatureStreamOptions>>(),
                sp.GetRequiredService<ILogger<FeatureStreamSessionManager>>(),
                sp.GetRequiredService<FeatureStreamMetrics>(),
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
        // Broker-agnostic event-bus sinks (#357). The publish path fans committed
        // feature-change events out to every registered sink through the broadcaster.
        // Sinks are off by default; the no-op sink keeps the fan-out path exercised
        // and observable. The concrete Kafka and NATS JetStream adapters below
        // register additional sinks when enabled (they require a live broker).
        services.AddOptions<FeatureChangeEventSinkOptions>()
            .Bind(configuration.GetSection(FeatureChangeEventSinkOptions.SectionName));
        services.AddSingleton<IFeatureChangeEventSink, NoOpFeatureChangeEventSink>();

        // Kafka sink adapter (#357). Bound and validated always, but the producer
        // and sink are only registered when the adapter is enabled so deployments
        // without Kafka never construct a broker client. The sink publishes with an
        // idempotent producer (producer-side exactly-once) and routes failed
        // deliveries to a dead-letter topic.
        services.AddOptions<KafkaFeatureChangeEventSinkOptions>()
            .Bind(configuration.GetSection(KafkaFeatureChangeEventSinkOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<KafkaFeatureChangeEventSinkOptions>,
            KafkaFeatureChangeEventSinkOptionsValidator>();
        if (configuration.GetValue<bool>($"{KafkaFeatureChangeEventSinkOptions.SectionName}:Enabled"))
        {
            services.AddSingleton<IKafkaEventProducer, ConfluentKafkaEventProducer>();
            services.AddSingleton<IFeatureChangeEventSink, KafkaFeatureChangeEventSink>();
        }

        // NATS JetStream sink adapter (#357). Bound and validated always, but the
        // producer and sink are only registered when the adapter is enabled so
        // deployments without NATS never open a JetStream connection. The sink
        // publishes with optional message deduplication (publish-side
        // exactly-once) and routes failed deliveries to a dead-letter subject.
        services.AddOptions<NatsFeatureChangeEventSinkOptions>()
            .Bind(configuration.GetSection(NatsFeatureChangeEventSinkOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<NatsFeatureChangeEventSinkOptions>,
            NatsFeatureChangeEventSinkOptionsValidator>();
        if (configuration.GetValue<bool>($"{NatsFeatureChangeEventSinkOptions.SectionName}:Enabled"))
        {
            services.AddSingleton<INatsEventProducer, NatsJetStreamEventProducer>();
            services.AddSingleton<IFeatureChangeEventSink, NatsFeatureChangeEventSink>();
        }

        services.AddSingleton<FeatureChangeEventSinkBroadcaster>();
        services.AddSingleton<IFeatureChangeEventPublisher>(sp =>
            new FeatureStreamPublisher(
                sp.GetRequiredService<IFeatureChangeEventStore>(),
                sp.GetRequiredService<FeatureStreamSessionManager>(),
                sp.GetRequiredService<ILogger<FeatureStreamPublisher>>(),
                sp.GetRequiredService<IFeatureChangeRetryQueue>(),
                sp.GetRequiredService<FeatureChangeEventSinkBroadcaster>()));
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
        services.AddSingleton<OutboxMetrics>();
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

    [LoggerMessage(
        EventId = 5110,
        Level = LogLevel.Warning,
        Message = "No Redis is configured; feature-change events use node-local in-memory storage (single-node mode). " +
                  "Events do not survive restarts and are not shared across nodes. " +
                  "Configure ConnectionStrings:redis for durable multi-node event storage.")]
    private static partial void LogSingleNodeInMemoryEventStore(ILogger logger);
}
