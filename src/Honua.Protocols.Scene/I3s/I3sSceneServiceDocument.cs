// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Scene.Conversion;

namespace Honua.Protocols.Scene.I3s;

internal sealed class I3sSceneServiceDocument
{
    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; } = string.Empty;

    [JsonPropertyName("serviceVersion")]
    public string ServiceVersion { get; set; } = "1.7";

    [JsonPropertyName("supportedBindings")]
    public string[] SupportedBindings { get; set; } = ["REST"];

    [JsonPropertyName("layers")]
    public IReadOnlyList<I3sSceneLayerDocument> Layers { get; set; } = [];
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(I3sSceneServiceDocument))]
[JsonSerializable(typeof(I3sSceneLayerDocument))]
[JsonSerializable(typeof(I3sSpatialReference))]
[JsonSerializable(typeof(I3sFullExtent))]
[JsonSerializable(typeof(I3sStore))]
[JsonSerializable(typeof(I3sNodePageDefinition))]
[JsonSerializable(typeof(I3sHeightModelInfo))]
[JsonSerializable(typeof(I3sAttributeStorageInfo))]
[JsonSerializable(typeof(I3sGeometryDefinition))]
[JsonSerializable(typeof(I3sMaterialDefinition))]
[JsonSerializable(typeof(I3sTextureSetDefinition))]
internal sealed partial class I3sServingJsonContext : JsonSerializerContext
{
}
