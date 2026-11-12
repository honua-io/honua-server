// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
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

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

public sealed record WorkTaskCompletionCommand(Guid TaskId, WorkTaskCompletionRequest Request, WorkTaskActor Actor, AssetCommand Command);
