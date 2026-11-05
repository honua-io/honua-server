// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Temporal;

public sealed class TemporalRollbackPlanRequestBody
{
    public TemporalCheckpointBody? Checkpoint { get; init; }
}

public sealed class TemporalRollbackExecuteRequestBody
{
    public TemporalCheckpointBody? Checkpoint { get; init; }

    // Approval is required when the plan demands it.
    public bool Approved { get; init; }

    public string? Reason { get; init; }
}

public sealed class TemporalCheckpointBody
{
    public string? Kind { get; init; }

    public string? Value { get; init; }

    public long? Generation { get; init; }
}

public sealed class TemporalCheckpointResponse
{
    public required string Kind { get; init; }

    public string? Value { get; init; }

    public required long Generation { get; init; }
}

public sealed class TemporalAttributionResponse
{
    public string? Actor { get; init; }

    public required string Source { get; init; }

    public string? Operation { get; init; }

    public string? SourceId { get; init; }
}

public sealed class TemporalFieldChangeResponse
{
    public required string Field { get; init; }

    public object? OldValue { get; init; }

    public object? NewValue { get; init; }

    public required bool Masked { get; init; }
}

public sealed class TemporalFeatureDiffResponse
{
    public required long ObjectId { get; init; }

    public required string PrimaryClass { get; init; }

    public required IReadOnlyList<string> Classes { get; init; }

    public required bool GeometryChanged { get; init; }

    public required IReadOnlyList<TemporalFieldChangeResponse> FieldChanges { get; init; }

    public TemporalAttributionResponse? Attribution { get; init; }
}

public sealed class TemporalDiffSummaryResponse
{
    public required int Added { get; init; }

    public required int Removed { get; init; }

    public required int AttributeChanged { get; init; }

    public required int GeometryChanged { get; init; }

    public required int Total { get; init; }
}

public sealed class TemporalDiffResponse
{
    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required TemporalCheckpointResponse From { get; init; }

    public required TemporalCheckpointResponse To { get; init; }

    public required TemporalDiffSummaryResponse Summary { get; init; }

    public required IReadOnlyList<TemporalFeatureDiffResponse> Changes { get; init; }

    public string? NextCursor { get; init; }
}

public sealed class TemporalRevisionResponse
{
    public required long Generation { get; init; }

    public required string Operation { get; init; }

    public required string ChangedAt { get; init; }

    public TemporalAttributionResponse? Attribution { get; init; }
}

public sealed class TemporalTimelineResponse
{
    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required long ObjectId { get; init; }

    public required long CurrentGeneration { get; init; }

    public required IReadOnlyList<TemporalRevisionResponse> Revisions { get; init; }

    public string? NextCursor { get; init; }
}

public sealed class TemporalRollbackFindingResponse
{
    public required string Code { get; init; }

    public required string Severity { get; init; }

    public required string Message { get; init; }
}

public sealed class TemporalRollbackPlanResponse
{
    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required TemporalCheckpointResponse TargetCheckpoint { get; init; }

    public required long CurrentGeneration { get; init; }

    public required string State { get; init; }

    public required int AffectedFeatureCount { get; init; }

    public required IReadOnlyList<TemporalRollbackFindingResponse> ValidationFindings { get; init; }

    public required IReadOnlyList<TemporalRollbackFindingResponse> CompatibilityFindings { get; init; }

    public required bool RequiresApproval { get; init; }
}

public sealed class TemporalRollbackJobResponse
{
    public required string JobId { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required TemporalCheckpointResponse TargetCheckpoint { get; init; }

    public required string Status { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TemporalRollbackPlanRequestBody))]
[JsonSerializable(typeof(TemporalRollbackExecuteRequestBody))]
[JsonSerializable(typeof(TemporalCheckpointBody))]
[JsonSerializable(typeof(TemporalCheckpointResponse))]
[JsonSerializable(typeof(TemporalAttributionResponse))]
[JsonSerializable(typeof(TemporalFieldChangeResponse))]
[JsonSerializable(typeof(TemporalFeatureDiffResponse))]
[JsonSerializable(typeof(TemporalDiffSummaryResponse))]
[JsonSerializable(typeof(TemporalDiffResponse))]
[JsonSerializable(typeof(TemporalRevisionResponse))]
[JsonSerializable(typeof(TemporalTimelineResponse))]
[JsonSerializable(typeof(TemporalRollbackFindingResponse))]
[JsonSerializable(typeof(TemporalRollbackPlanResponse))]
[JsonSerializable(typeof(TemporalRollbackJobResponse))]
[JsonSerializable(typeof(ProblemDetailsResponse))]
// The diff field-change values flow through as object?; register the primitive value types so the
// polymorphic serializer can emit them under AOT (mirrors TemporalHistoryApiJsonContext).
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
internal sealed partial class TemporalHistorySlicesJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
