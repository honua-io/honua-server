// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.ControlPlane.Lambda;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(BatchJobStateChangeEvent))]
[JsonSerializable(typeof(BatchJobStateChangeDetail))]
[JsonSerializable(typeof(BackstopTickEvent))]
[JsonSerializable(typeof(ScheduledTickEvent))]
internal sealed partial class ControlPlaneLambdaJsonContext : JsonSerializerContext
{
}

internal sealed class BackstopTickEvent
{
}

internal sealed class ScheduledTickEvent
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }
}
