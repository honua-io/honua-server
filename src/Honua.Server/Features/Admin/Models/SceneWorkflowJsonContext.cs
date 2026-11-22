// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SceneArchiveIngestResponse))]
[JsonSerializable(typeof(SceneModelIngestResponse))]
[JsonSerializable(typeof(SceneJobAcceptedResponse))]
[JsonSerializable(typeof(SceneJobStatusResponse))]
[JsonSerializable(typeof(SceneJobStatusResponse[]))]
[JsonSerializable(typeof(SceneAssetLifecycleResponse))]
[JsonSerializable(typeof(SceneAssetLifecycleResponse[]))]
internal sealed partial class SceneWorkflowJsonContext : JsonSerializerContext
{
}
