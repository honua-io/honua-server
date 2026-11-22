// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Warehouse.Domain;

public enum WarehouseSourceAvailability
{
    Available,

    Disabled,

    Unavailable,

    Unsupported
}

public sealed record WarehouseCrsBehavior
{
    public required bool SupportsCrossSridFilter { get; init; }

    public required bool SupportsOutputReprojection { get; init; }

    public required bool AssumesWgs84WhenUnset { get; init; }

    public required string AxisOrder { get; init; }

    public required string Notes { get; init; }
}

public sealed record WarehouseAggregationCapabilities
{
    public required bool SupportsStatistics { get; init; }

    public required bool SupportsGroupBy { get; init; }

    public required bool SupportsHaving { get; init; }

    public required bool SupportsH3Aggregation { get; init; }

    public required bool SupportsH3Polyfill { get; init; }

    public required bool SupportsH3KRing { get; init; }

    public ImmutableArray<string> RequiredExtensions { get; init; } = [];
}

public sealed record WarehouseSourceCapabilityRequest
{
    public required string ProviderName { get; init; }

    public required string SourceId { get; init; }

    public string? ExternalSourceUri { get; init; }

    public bool HasBboxCoveringColumn { get; init; }

    public bool? RuntimeEnabled { get; init; }

    public bool? RuntimeReachable { get; init; }

    public string? UnavailableReason { get; init; }

    public override string ToString()
        => $"WarehouseSourceCapabilityRequest {{ ProviderName = {ProviderName}, SourceId = {SourceId} }}";
}

public sealed record WarehouseEffectiveSourceCapabilities
{
    public required string ProviderName { get; init; }

    public required string SourceId { get; init; }

    public required WarehouseSourceAvailability Availability { get; init; }

    public string? AvailabilityReason { get; init; }

    public required bool ReadOnly { get; init; }

    public required bool SupportsBboxPushdown { get; init; }

    public required bool SupportsTemporalFilter { get; init; }

    public required bool SupportsSqlFilterPushdown { get; init; }

    public required bool SupportsEnforcedPolicyPushdown { get; init; }

    public required ImmutableArray<SpatialRelationship> SupportedSpatialPredicates { get; init; }

    public required WarehouseCrsBehavior CrsBehavior { get; init; }

    public required WarehouseAggregationCapabilities Aggregation { get; init; }

    public ImmutableArray<string> RequiredExtensions { get; init; } = [];

    public DateTimeOffset EvaluatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string ToJson()
        => JsonSerializer.Serialize(this, WarehouseJsonContext.Default.WarehouseEffectiveSourceCapabilities);

    public override string ToString()
        => $"WarehouseEffectiveSourceCapabilities {{ ProviderName = {ProviderName}, SourceId = {SourceId}, Availability = {Availability}, ReadOnly = {ReadOnly} }}";
}
