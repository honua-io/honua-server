// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Assets;

public sealed record WorkTaskCompletionRequest
{
    [JsonPropertyName("commandId")]
    public Guid? CommandId { get; init; }

    [JsonPropertyName("expectedVersion")]
    public long? ExpectedVersion { get; init; }

    // Informational only: a stale downloaded summary never rejects a completion.
    [JsonPropertyName("summaryRevision")]
    public long? SummaryRevision { get; init; }

    [JsonPropertyName("observedAt")]
    public DateTimeOffset? ObservedAt { get; init; }

    [JsonPropertyName("form")]
    public WorkTaskFormReference? Form { get; init; }

    [JsonPropertyName("condition")]
    public int? Condition { get; init; }

    [JsonPropertyName("answers")]
    public JsonElement? Answers { get; init; }

    [JsonPropertyName("attachments")]
    public Guid[] Attachments { get; init; } = [];

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

public sealed record WorkTaskCompletionCommand(Guid TaskId, WorkTaskCompletionRequest Request, WorkTaskActor Actor)
{
    public required AssetCommand Command { get; init; }
    public required long ConfigurationGeneration { get; init; }
    public WorkTaskAssetWriteback? AssetWriteback { get; init; }
}

public sealed record WorkTaskAssetWriteback(long ProfileVersion, int StorageLayerId, FeatureEditBatch? Batch);

public sealed record WorkTaskCompletionConflict
{
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = AssetCommandOutcome.Conflict;

    [JsonPropertyName("commandId")]
    public required Guid CommandId { get; init; }

    [JsonPropertyName("candidateId")]
    public required Guid CandidateId { get; init; }

    [JsonPropertyName("taskId")]
    public required Guid TaskId { get; init; }

    [JsonPropertyName("currentVersion")]
    public required long CurrentVersion { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "The task is already completed; the observation was retained for review.";

    [JsonPropertyName("candidate")]
    public required WorkTaskCompletionRequest Candidate { get; init; }
}
