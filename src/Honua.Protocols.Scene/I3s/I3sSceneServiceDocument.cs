// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Scene.Conversion;

namespace Honua.Protocols.Scene.I3s;

/// <summary>
/// Esri I3S <c>SceneServer</c> service descriptor returned from the
/// <c>/scenes/{sceneId}/SceneServer</c> root. It advertises the service
/// version and the set of scene layers an ArcGIS / I3S client can load.
/// </summary>
public sealed class I3sSceneServiceDocument
{
    /// <summary>Service name (the hosting scene's display name).</summary>
    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>I3S service version advertised to clients.</summary>
    [JsonPropertyName("serviceVersion")]
    public string ServiceVersion { get; set; } = "1.7";

    /// <summary>Supported bindings; Honua serves the REST binding only.</summary>
    [JsonPropertyName("supportedBindings")]
    public string[] SupportedBindings { get; set; } = ["REST"];

    /// <summary>Scene layers exposed by this service.</summary>
    [JsonPropertyName("layers")]
    public IReadOnlyList<I3sSceneLayerDocument> Layers { get; set; } = [];
}

/// <summary>
/// Source-generated JSON context for the I3S SceneServer serving surface.
/// Serializes both the service descriptor and the per-layer descriptor with
/// I3S-compatible (PascalCase-preserving) member names sourced from the
/// declared <c>JsonPropertyName</c> attributes.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(I3sSceneServiceDocument))]
[JsonSerializable(typeof(I3sSceneLayerDocument))]
[JsonSerializable(typeof(I3sSpatialReference))]
[JsonSerializable(typeof(I3sFullExtent))]
[JsonSerializable(typeof(I3sStore))]
public sealed partial class I3sServingJsonContext : JsonSerializerContext
{
}
