// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.SensorThings.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Protocols.SensorThings;

/// <summary>
/// Registers OGC SensorThings API (STA v1.1) services in the dependency injection container.
/// </summary>
internal static class SensorThingsServiceCollectionExtensions
{
    /// <summary>
    /// Adds SensorThings API services. The observations store
    /// (<see cref="Honua.Core.Features.SensorThings.Abstractions.IObservationStore"/>)
    /// is registered by the active data provider.
    /// </summary>
    public static IServiceCollection AddSensorThings(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<StaObservationFilterTranslator>();

        return services;
    }
}
