// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Temporal;

public sealed class TemporalCapabilityResponse
{
    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public string? LayerName { get; init; }

    public required bool SupportsHistory { get; init; }

    public required bool SupportsAsOf { get; init; }

    public string? TemporalColumn { get; init; }

    public required string CursorKind { get; init; }

    public long? CurrentGeneration { get; init; }

    public required TemporalDeferredCapabilitiesResponse Deferred { get; init; }
}

public sealed class TemporalDeferredCapabilitiesResponse
{
    public required bool SupportsDiff { get; init; }

    public required bool SupportsTimeline { get; init; }

    public required bool SupportsAttribution { get; init; }

    public required bool SupportsRollback { get; init; }
}

public sealed class TemporalAsOfResponse
{
    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required string RequestedCursorKind { get; init; }

    public required long ResolvedGeneration { get; init; }

    public required long CurrentGeneration { get; init; }

    public required IReadOnlyList<TemporalFeatureStateResponse> Features { get; init; }
}

public sealed class TemporalFeatureStateResponse
{
    public required long ObjectId { get; init; }

    public required string Operation { get; init; }

    public required string ChangedAt { get; init; }

    public IReadOnlyDictionary<string, object?>? Attributes { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TemporalCapabilityResponse))]
[JsonSerializable(typeof(TemporalDeferredCapabilitiesResponse))]
[JsonSerializable(typeof(TemporalAsOfResponse))]
[JsonSerializable(typeof(TemporalFeatureStateResponse))]
[JsonSerializable(typeof(ProblemDetailsResponse))]
// The feature attribute bag flows through as IReadOnlyDictionary<string, object?>; register the
// concrete dictionary plus the primitive value types so the polymorphic serializer can emit them
// under AOT (mirrors SpatialAnalyticsJsonContext).
[JsonSerializable(typeof(IReadOnlyDictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(byte[]))]
internal sealed partial class TemporalHistoryApiJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
