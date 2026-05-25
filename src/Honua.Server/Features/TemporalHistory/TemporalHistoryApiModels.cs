// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.TemporalHistory.Domain;

namespace Honua.Server.Features.TemporalHistory;

/// <summary>
/// Response envelope for the checkpoint enumeration endpoint.
/// </summary>
public sealed record TemporalCheckpointListResponse
{
    /// <summary>
    /// Stable identifier of the layer.
    /// </summary>
    public required long LayerId { get; init; }

    /// <summary>
    /// Available checkpoints, newest first.
    /// </summary>
    public IReadOnlyList<TemporalCheckpoint> Checkpoints { get; init; } = [];
}

/// <summary>
/// Request body for executing an approved rollback.
/// </summary>
public sealed record TemporalRollbackRequestBody
{
    /// <summary>
    /// Opaque target cursor token to roll back to.
    /// </summary>
    public string? To { get; init; }

    /// <summary>
    /// Explicit approval flag. Rollback execution is rejected unless true.
    /// </summary>
    public bool Approved { get; init; }

    /// <summary>
    /// Operator-supplied reason recorded with the corrective operation.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Response returned when a rollback has been accepted and enqueued through the job runner.
/// </summary>
public sealed record TemporalRollbackAcceptedResponse
{
    /// <summary>
    /// Stable identifier of the layer.
    /// </summary>
    public required long LayerId { get; init; }

    /// <summary>
    /// Identifier of the job-run that will apply the corrective operation.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Opaque target cursor the rollback restores.
    /// </summary>
    public required string To { get; init; }

    /// <summary>
    /// Job status at acceptance time.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Rollback mode reported by the validating plan.
    /// </summary>
    public required string Mode { get; init; }

    /// <summary>
    /// Number of features the corrective operation is expected to affect.
    /// </summary>
    public int AffectedCount { get; init; }
}

/// <summary>
/// AOT source-generated JSON context for the temporal-history API surface. All response models use
/// camelCase, omit nulls, and serialize enums as strings.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(TemporalSourceCapabilityInfo))]
[JsonSerializable(typeof(TemporalSnapshot))]
[JsonSerializable(typeof(TemporalDiff))]
[JsonSerializable(typeof(TemporalTimeline))]
[JsonSerializable(typeof(TemporalRollbackPlan))]
[JsonSerializable(typeof(TemporalCheckpointListResponse))]
[JsonSerializable(typeof(TemporalRollbackRequestBody))]
[JsonSerializable(typeof(TemporalRollbackAcceptedResponse))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, JsonElement>))]
internal sealed partial class TemporalHistoryApiJsonContext : JsonSerializerContext
{
}
