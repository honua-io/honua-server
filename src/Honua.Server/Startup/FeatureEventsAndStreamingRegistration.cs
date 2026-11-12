// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Events.Outbox;
using Honua.Infrastructure.Extensions;
using Honua.Server.Features.Realtime;
using Honua.Server.Features.Streaming;
using Honua.Server.Features.Streaming.Conformance;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Startup;

// Feature-change events are read from the PostgreSQL change log (ADR-0085); streams follow it through the realtime fan-out provider.
internal static class FeatureEventsAndStreamingRegistration
{
    public static IServiceCollection AddHonuaFeatureEventsAndStreaming(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<FeatureChangeEventOptions>(
            configuration.GetSection(FeatureChangeEventOptions.SectionName));
        services.AddSingleton<IValidateOptions<FeatureChangeWebhookOptions>, FeatureChangeWebhookOptionsValidator>();
        services.AddOptions<FeatureChangeWebhookOptions>()
            .Bind(configuration.GetSection(FeatureChangeWebhookOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => new PostgresFeatureChangeLog(
            sp.GetRequiredService<JobStoreDatabase>(),
            sp.GetRequiredService<IOptions<RealtimeOptions>>(),
            TimeProvider.System));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRealtimeFamilySource, PostgresFeatureChangeLog>(
            sp => sp.GetRequiredService<PostgresFeatureChangeLog>()));
        services.AddSingleton<PostgresFeatureChangeEventStore>();
        services.AddSingleton<IFeatureChangeEventStore>(sp => sp.GetRequiredService<PostgresFeatureChangeEventStore>());
        services.AddSingleton<IFeatureChangeEventStoreHealth>(sp => sp.GetRequiredService<PostgresFeatureChangeEventStore>());
        services.AddSingleton<IStreamCursorStore, PostgresStreamCursorStore>();

        // Feature-stream session manager and streaming publisher (#501)
        services.AddOptions<FeatureStreamOptions>()
            .Bind(configuration.GetSection(FeatureStreamOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FeatureStreamOptions>, FeatureStreamOptionsValidator>();
        services.AddSingleton<FeatureStreamMetrics>();
        services.AddSingleton<FeatureStreamRoutabilityGuard>();
        services.AddSingleton<FeatureStreamSessionManager>(sp =>
            new FeatureStreamSessionManager(
                sp.GetRequiredService<IOptions<FeatureStreamOptions>>(),
                sp.GetRequiredService<ILogger<FeatureStreamSessionManager>>(),
                sp.GetRequiredService<FeatureStreamMetrics>(),
                routabilityGuard: sp.GetRequiredService<FeatureStreamRoutabilityGuard>(),
                serviceScopeFactory: sp.GetRequiredService<IServiceScopeFactory>()));
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
                sp.GetRequiredService<IRealtimeWakeSignal>(),
                sp.GetRequiredService<PostgresFeatureChangeLog>(),
                sp.GetRequiredService<ILogger<FeatureStreamPublisher>>(),
                sp.GetRequiredService<FeatureChangeEventSinkBroadcaster>()));
        services.AddSingleton<FeatureMutationEventService>();

        // Feature-change transactional outbox: capability provider, dispatcher, health,
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
        // The dispatcher opens a database connection on each pass. On a control-plane event
        // Lambda that exhausts the pool of 2 and pins the connection across freeze. The
        // singleton stays so health can read it.
        if (ControlPlaneLambdaConnectionRelease.HostsDatabasePollingLoops(configuration))
        {
            services.AddHostedService(sp =>
                sp.GetRequiredService<OutboxDispatcherBackgroundService>());
        }
        services.AddScoped<FeatureStreamDependencies>();
        services.AddHostedService<FeatureStreamHeartbeatService>();
        if (ControlPlaneLambdaConnectionRelease.HostsDatabasePollingLoops(configuration))
        {
            services.AddHostedService<FeatureStreamRealtimeRelay>();
        }

        // Controlled-conformance mutation workflow (#3038). Options are bound and validated
        // unconditionally so a misconfigured source fails at startup, but the lease registry,
        // workflow, and TTL sweeper only participate when an operator has deliberately
        // provisioned a dedicated conformance source: the surface fails closed otherwise.
        services.AddOptions<FeatureStreamConformanceOptions>()
            .Bind(configuration.GetSection(FeatureStreamConformanceOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<FeatureStreamConformanceOptions>,
            FeatureStreamConformanceOptionsValidator>();
        services.AddSingleton<FeatureStreamConformanceRunRegistry>();
        services.AddScoped<FeatureStreamConformanceStore>();
        services.AddScoped<FeatureStreamConformanceService>();
        services.AddHostedService<FeatureStreamConformanceSweepService>();
        services.AddResilientHttpClient(
            "feature-change-webhook",
            "feature-change-webhook",
            HttpResiliencePolicies.FastApiDefaults,
            configureHandler: static () => WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler());
        services.AddHostedService(sp =>
            new FeatureChangeWebhookDispatcher(
                sp.GetRequiredService<IFeatureChangeEventStore>(),
                sp.GetRequiredService<IStreamCursorStore>(),
                sp.SharedLeaseStore(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IOptions<FeatureChangeWebhookOptions>>(),
                sp.GetRequiredService<ILogger<FeatureChangeWebhookDispatcher>>()));

        return services;
    }
}
