// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Temporal;

/// <summary>
/// Request body wrapper carrying a target checkpoint for rollback planning (slice 5 of #1166).
/// </summary>
public sealed class TemporalRollbackPlanRequestBody
{
    /// <summary>The target checkpoint to restore the layer to.</summary>
    public TemporalCheckpointBody? Checkpoint { get; init; }
}

/// <summary>
/// Request body for rollback execution (slice 5 of #1166).
/// </summary>
public sealed class TemporalRollbackExecuteRequestBody
{
    /// <summary>The target checkpoint to restore the layer to.</summary>
    public TemporalCheckpointBody? Checkpoint { get; init; }

    /// <summary>True when the operator approves the rollback (required when the plan demands approval).</summary>
    public bool Approved { get; init; }

    /// <summary>Operator-supplied reason recorded in the corrective operation's attribution.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// A checkpoint reference supplied in a request body: a kind plus either a raw value or a numeric
/// generation (slices 2-5 of #1166).
/// </summary>
public sealed class TemporalCheckpointBody
{
    /// <summary>Checkpoint kind: generation/timestamp/transaction/release/job/editSession/named.</summary>
    public string? Kind { get; init; }

    /// <summary>Raw checkpoint value (timestamp/transaction/release/job/edit-session/named identifier).</summary>
    public string? Value { get; init; }

    /// <summary>Numeric generation for a generation-kind checkpoint.</summary>
    public long? Generation { get; init; }
}

/// <summary>
/// A resolved checkpoint surfaced in temporal responses (slices 2-5 of #1166).
/// </summary>
public sealed class TemporalCheckpointResponse
{
    /// <summary>The checkpoint kind the client requested.</summary>
    public required string Kind { get; init; }

    /// <summary>The raw checkpoint value, when applicable.</summary>
    public string? Value { get; init; }

    /// <summary>The change-tracker generation the checkpoint resolved to.</summary>
    public required long Generation { get; init; }
}

/// <summary>
/// Attribution surfaced on diffs and timeline revisions (slice 4 of #1166).
/// </summary>
public sealed class TemporalAttributionResponse
{
    /// <summary>Principal that produced the change, when recorded.</summary>
    public string? Actor { get; init; }

    /// <summary>Attribution source category.</summary>
    public required string Source { get; init; }

    /// <summary>Higher-level operation name, when recorded.</summary>
    public string? Operation { get; init; }

    /// <summary>Producing source correlation id (job/edit-session/release/audit id), when recorded.</summary>
    public string? SourceId { get; init; }
}

/// <summary>
/// A field-level change detail within a feature diff (slice 2 of #1166).
/// </summary>
public sealed class TemporalFieldChangeResponse
{
    /// <summary>The attribute name that changed.</summary>
    public required string Field { get; init; }

    /// <summary>The value at the base checkpoint (null when added or masked).</summary>
    public object? OldValue { get; init; }

    /// <summary>The value at the target checkpoint (null when removed or masked).</summary>
    public object? NewValue { get; init; }

    /// <summary>True when the field is redacted by the masking policy.</summary>
    public required bool Masked { get; init; }
}

/// <summary>
/// A single feature's classified change in a diff (slice 2 of #1166).
/// </summary>
public sealed class TemporalFeatureDiffResponse
{
    /// <summary>Stable object id.</summary>
    public required long ObjectId { get; init; }

    /// <summary>Dominant change class used for summary grouping.</summary>
    public required string PrimaryClass { get; init; }

    /// <summary>All change classes that apply to the feature.</summary>
    public required IReadOnlyList<string> Classes { get; init; }

    /// <summary>True when the feature's geometry changed.</summary>
    public required bool GeometryChanged { get; init; }

    /// <summary>Field-level attribute changes.</summary>
    public required IReadOnlyList<TemporalFieldChangeResponse> FieldChanges { get; init; }

    /// <summary>Attribution for the change, when recorded.</summary>
    public TemporalAttributionResponse? Attribution { get; init; }
}

/// <summary>
/// Summary counts for a temporal diff (slice 2 of #1166).
/// </summary>
public sealed class TemporalDiffSummaryResponse
{
    /// <summary>Number of features added.</summary>
    public required int Added { get; init; }

    /// <summary>Number of features removed.</summary>
    public required int Removed { get; init; }

    /// <summary>Number of features whose attributes changed.</summary>
    public required int AttributeChanged { get; init; }

    /// <summary>Number of features whose geometry changed.</summary>
    public required int GeometryChanged { get; init; }

    /// <summary>Total number of distinct changed features.</summary>
    public required int Total { get; init; }
}

/// <summary>
/// Diff result returned by the temporal diff endpoint (slice 2 of #1166).
/// </summary>
public sealed class TemporalDiffResponse
{
    /// <summary>Owning service id.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Service-local layer index.</summary>
    public required int LayerId { get; init; }

    /// <summary>The resolved base checkpoint.</summary>
    public required TemporalCheckpointResponse From { get; init; }

    /// <summary>The resolved target checkpoint.</summary>
    public required TemporalCheckpointResponse To { get; init; }

    /// <summary>Summary counts across the full diff.</summary>
    public required TemporalDiffSummaryResponse Summary { get; init; }

    /// <summary>A page of classified feature changes.</summary>
    public required IReadOnlyList<TemporalFeatureDiffResponse> Changes { get; init; }

    /// <summary>Opaque cursor for the next page, or null when this is the last page.</summary>
    public string? NextCursor { get; init; }
}

/// <summary>
/// A single revision in a feature timeline (slice 3 of #1166).
/// </summary>
public sealed class TemporalRevisionResponse
{
    /// <summary>The change-tracker generation at which the revision was recorded.</summary>
    public required long Generation { get; init; }

    /// <summary>The operation that produced the revision.</summary>
    public required string Operation { get; init; }

    /// <summary>When the revision was recorded, in UTC ISO-8601.</summary>
    public required string ChangedAt { get; init; }

    /// <summary>Attribution for the revision, when recorded.</summary>
    public TemporalAttributionResponse? Attribution { get; init; }
}

/// <summary>
/// Feature timeline result returned by the timeline endpoint (slice 3 of #1166).
/// </summary>
public sealed class TemporalTimelineResponse
{
    /// <summary>Owning service id.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Service-local layer index.</summary>
    public required int LayerId { get; init; }

    /// <summary>Stable object id.</summary>
    public required long ObjectId { get; init; }

    /// <summary>Current change-tracker generation at read time.</summary>
    public required long CurrentGeneration { get; init; }

    /// <summary>Ordered revisions (oldest first), subject to masking.</summary>
    public required IReadOnlyList<TemporalRevisionResponse> Revisions { get; init; }

    /// <summary>Opaque cursor for the next page, or null when this is the last page.</summary>
    public string? NextCursor { get; init; }
}

/// <summary>
/// A rollback planning finding (slice 5 of #1166).
/// </summary>
public sealed class TemporalRollbackFindingResponse
{
    /// <summary>Stable machine-readable finding code.</summary>
    public required string Code { get; init; }

    /// <summary>Finding severity: info/warning/error.</summary>
    public required string Severity { get; init; }

    /// <summary>Operator-facing description.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// Rollback plan returned by the rollback plan endpoint (slice 5 of #1166).
/// </summary>
public sealed class TemporalRollbackPlanResponse
{
    /// <summary>Owning service id.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Service-local layer index.</summary>
    public required int LayerId { get; init; }

    /// <summary>The resolved checkpoint the rollback would restore the layer to.</summary>
    public required TemporalCheckpointResponse TargetCheckpoint { get; init; }

    /// <summary>Current change-tracker generation at planning time.</summary>
    public required long CurrentGeneration { get; init; }

    /// <summary>The rollback disposition: supported/blocked/scriptRequired/jobRequired/manual.</summary>
    public required string State { get; init; }

    /// <summary>Number of features the corrective operation would touch.</summary>
    public required int AffectedFeatureCount { get; init; }

    /// <summary>Validation findings.</summary>
    public required IReadOnlyList<TemporalRollbackFindingResponse> ValidationFindings { get; init; }

    /// <summary>Compatibility findings.</summary>
    public required IReadOnlyList<TemporalRollbackFindingResponse> CompatibilityFindings { get; init; }

    /// <summary>True when the rollback requires explicit approval before execution.</summary>
    public required bool RequiresApproval { get; init; }
}

/// <summary>
/// Job handle returned by the rollback execution endpoint (slice 5 of #1166).
/// </summary>
public sealed class TemporalRollbackJobResponse
{
    /// <summary>Stable job id for polling the job runner.</summary>
    public required string JobId { get; init; }

    /// <summary>Owning service id.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Service-local layer index.</summary>
    public required int LayerId { get; init; }

    /// <summary>The resolved checkpoint the rollback restores the layer to.</summary>
    public required TemporalCheckpointResponse TargetCheckpoint { get; init; }

    /// <summary>The job status at submission time.</summary>
    public required string Status { get; init; }
}

/// <summary>
/// Source-generated JSON context for the temporal history slice 2-5 admin API (AOT).
/// </summary>
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
