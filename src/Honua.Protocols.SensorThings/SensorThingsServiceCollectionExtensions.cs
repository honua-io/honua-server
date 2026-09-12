// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Protocols.SensorThings.Services;
using Honua.Protocols.SensorThings.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Protocols.SensorThings;

/// <summary>
/// Registers OGC SensorThings API (STA v1.1) services in the dependency injection container.
/// </summary>
internal static class SensorThingsServiceCollectionExtensions
{
    /// <summary>
    /// Adds SensorThings API services. The observations store
    /// (<see cref="Honua.Core.Features.SensorThings.Abstractions.IObservationStore"/>)
    /// is registered by the active data provider. The Phase 2 ingest path publishes new
    /// observations to the Phase 3 real-time stream through
    /// <see cref="IObservationChangeEventPublisher"/>, implemented by a scoped publisher
    /// feeding the singleton <see cref="ObservationStreamSessionManager"/> (Redis cross-node fan-out when a
    /// multiplexer is registered, single-node otherwise).
    /// </summary>
    public static IServiceCollection AddSensorThings(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<StaFilterTranslator>();

        // Per-principal, per-tenant and per-node admission caps (#4198). Validated at
        // startup so a per-scope cap can never be configured at or above the node cap.
        services.AddOptions<ObservationStreamOptions>()
            .BindConfiguration(ObservationStreamOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ObservationStreamOptions>, ObservationStreamOptionsValidator>());

        // Capture tenant/schema only after request middleware resolves them. The
        // singleton transport must never hold request services or read ambient state.
        services.TryAddSingleton(sp => new ObservationStreamSessionManager(
            sp.GetRequiredService<ILogger<ObservationStreamSessionManager>>(),
            sp.GetService<IConnectionMultiplexer>(),
            sp.GetRequiredService<IOptions<ObservationStreamOptions>>().Value));
        services.TryAddScoped(ObservationStreamScope.FromServices);
        services.TryAddScoped<IObservationChangeEventPublisher, ObservationStreamPublisher>();
        services.AddHostedService<ObservationStreamHeartbeatService>();

        return services;
    }
}
