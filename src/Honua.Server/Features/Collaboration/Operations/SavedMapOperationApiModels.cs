// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Collaboration.Operations;

internal sealed record SavedMapOperationAppendApiRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string? OperationId { get; init; }

    // Ignored for attribution, which comes from the authenticated principal, and kept so a client that sends it is not rejected as unknown JSON.
    public string? ActorId { get; init; }

    public long BaseCursor { get; init; }

    [Required]
    public SavedMapOperationKind? Kind { get; init; }

    public JsonElement Payload { get; init; }

    [StringLength(200)]
    public string? IdempotencyKey { get; init; }
}

internal sealed record SavedMapOperationEnvelopeDto
{
    public required string OperationId { get; init; }

    public required string MapId { get; init; }

    public required string ActorId { get; init; }

    public required long BaseCursor { get; init; }

    public required SavedMapOperationKind Kind { get; init; }

    public required long ServerCursor { get; init; }

    public required DateTimeOffset AcceptedAt { get; init; }

    public JsonElement Payload { get; init; }

    public string? IdempotencyKey { get; init; }
}

internal sealed record SavedMapOperationAppendApiResponse
{
    public required string Status { get; init; }

    public SavedMapOperationEnvelopeDto? Operation { get; init; }

    public required long HeadCursor { get; init; }

    public bool IsDuplicate { get; init; }

    public SavedMapOperationConflictDto? Conflict { get; init; }

    public string? Message { get; init; }
}

internal sealed record SavedMapOperationConflictDto
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public required long BaseCursor { get; init; }

    public required long HeadCursor { get; init; }

    public required IReadOnlyList<SavedMapOperationEnvelopeDto> ConflictingOperations { get; init; }
}

internal sealed record SavedMapOperationReplayApiResponse
{
    public required string Status { get; init; }

    public required long SinceCursor { get; init; }

    public required long HeadCursor { get; init; }

    public required long MinimumReplayCursor { get; init; }

    public required IReadOnlyList<SavedMapOperationEnvelopeDto> Operations { get; init; }

    public string? Message { get; init; }
}

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
