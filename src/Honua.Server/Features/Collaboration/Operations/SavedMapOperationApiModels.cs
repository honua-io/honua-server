// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Collaboration.Operations;

/// <summary>
/// Client request to append one saved-map collaborative edit operation (#972). The wire shape
/// flattens the typed core identifiers/cursors to JSON-friendly scalars.
/// </summary>
internal sealed record SavedMapOperationAppendApiRequest
{
    /// <summary>Client-generated operation id. Duplicates are replayed idempotently per map.</summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string? OperationId { get; init; }

    /// <summary>Actor submitting the operation. When omitted, the authenticated principal is used.</summary>
    public string? ActorId { get; init; }

    /// <summary>Client base cursor/revision the edit was authored against. Defaults to the empty-log base.</summary>
    public long BaseCursor { get; init; }

    /// <summary>Operation kind used by the MVP conflict policy and future WebMapDoc integration.</summary>
    [Required]
    public SavedMapOperationKind? Kind { get; init; }

    /// <summary>Operation payload. Stored opaquely by the MVP log.</summary>
    public JsonElement Payload { get; init; }

    /// <summary>Optional idempotency key. When supplied, duplicates are replayed idempotently per map.</summary>
    [StringLength(200)]
    public string? IdempotencyKey { get; init; }
}

/// <summary>Wire projection of an accepted saved-map operation envelope.</summary>
internal sealed record SavedMapOperationEnvelopeDto
{
    /// <summary>Client operation id.</summary>
    public required string OperationId { get; init; }

    /// <summary>Saved map id.</summary>
    public required string MapId { get; init; }

    /// <summary>Actor id.</summary>
    public required string ActorId { get; init; }

    /// <summary>Client base cursor used at append time.</summary>
    public required long BaseCursor { get; init; }

    /// <summary>Operation kind.</summary>
    public required SavedMapOperationKind Kind { get; init; }

    /// <summary>Monotonic server cursor assigned to the operation.</summary>
    public required long ServerCursor { get; init; }

    /// <summary>Server timestamp when the operation was accepted.</summary>
    public required DateTimeOffset AcceptedAt { get; init; }

    /// <summary>Operation payload.</summary>
    public JsonElement Payload { get; init; }

    /// <summary>Idempotency key, when supplied.</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>Append response: accepted operation plus the current head cursor and idempotency flag.</summary>
internal sealed record SavedMapOperationAppendApiResponse
{
    /// <summary>Append status: <c>accepted</c>, <c>conflict</c>, or <c>resyncRequired</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Accepted operation when status is <c>accepted</c>.</summary>
    public SavedMapOperationEnvelopeDto? Operation { get; init; }

    /// <summary>Current saved-map operation-log head cursor.</summary>
    public required long HeadCursor { get; init; }

    /// <summary>True when an accepted result was replayed from a duplicate id/idempotency key.</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>Conflict details when status is <c>conflict</c>.</summary>
    public SavedMapOperationConflictDto? Conflict { get; init; }

    /// <summary>Explanatory message for non-accepted results.</summary>
    public string? Message { get; init; }
}

/// <summary>Wire projection of a conflict response.</summary>
internal sealed record SavedMapOperationConflictDto
{
    /// <summary>Stable conflict code.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable conflict message.</summary>
    public required string Message { get; init; }

    /// <summary>Client base cursor that produced the conflict.</summary>
    public required long BaseCursor { get; init; }

    /// <summary>Current head cursor.</summary>
    public required long HeadCursor { get; init; }

    /// <summary>Concurrent operations that made the append unsafe.</summary>
    public required IReadOnlyList<SavedMapOperationEnvelopeDto> ConflictingOperations { get; init; }
}

/// <summary>Replay response: operations since a cursor plus replay-window metadata.</summary>
internal sealed record SavedMapOperationReplayApiResponse
{
    /// <summary>Replay status: <c>ok</c> or <c>resyncRequired</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Cursor the caller replayed after.</summary>
    public required long SinceCursor { get; init; }

    /// <summary>Current head cursor.</summary>
    public required long HeadCursor { get; init; }

    /// <summary>Earliest cursor the retained log can replay from.</summary>
    public required long MinimumReplayCursor { get; init; }

    /// <summary>Operations accepted after the requested cursor, ordered by server cursor.</summary>
    public required IReadOnlyList<SavedMapOperationEnvelopeDto> Operations { get; init; }

    /// <summary>Explanatory message for resync-required results.</summary>
    public string? Message { get; init; }
}

/// <summary>Source-generated JSON context for the saved-map operation-log API surface.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(SavedMapOperationAppendApiRequest))]
[JsonSerializable(typeof(SavedMapOperationAppendApiResponse))]
[JsonSerializable(typeof(SavedMapOperationReplayApiResponse))]
[JsonSerializable(typeof(SavedMapOperationEnvelopeDto))]
[JsonSerializable(typeof(SavedMapOperationConflictDto))]
[JsonSerializable(typeof(ApiResponse<SavedMapOperationAppendApiResponse>))]
[JsonSerializable(typeof(ApiResponse<SavedMapOperationReplayApiResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
internal sealed partial class SavedMapOperationJsonContext : JsonSerializerContext
{
}
