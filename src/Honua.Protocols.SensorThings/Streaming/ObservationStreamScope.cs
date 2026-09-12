// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Core.Features.SensorThings.Domain;

namespace Honua.Protocols.SensorThings.Streaming;

/// <summary>
/// Immutable routing boundary captured after tenant and schema resolution. Neither an
/// unresolved tenant nor the default schema is a wildcard. Schema is included because
/// observation identifiers are allocated independently within each database schema.
/// </summary>
internal sealed record ObservationStreamScope(string? TenantId, string? Schema)
{
    internal static ObservationStreamScope FromServices(IServiceProvider services) => new(
        services.GetService<ITenantContext>()?.TenantId,
        services.GetService<ISchemaContext>()?.CurrentSchema);
}

/// <summary>
/// Bridges request-scoped ingest to the singleton transport without letting the
/// transport resolve ambient tenant state (including on Redis callback threads).
/// </summary>
internal sealed class ObservationStreamPublisher(
    ObservationStreamSessionManager manager,
    ObservationStreamScope scope) : IObservationChangeEventPublisher
{
    public void PublishObservations(IReadOnlyList<SensorThingsObservation> observations) =>
        manager.PublishObservations(observations, scope);
}
