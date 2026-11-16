// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Scene.Conversion;

public sealed class I3sNodePageDocument
{
    [JsonPropertyName("nodes")]
    public IReadOnlyList<I3sNodePageEntry> Nodes { get; set; } = [];
}

public sealed class I3sNodePageEntry
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("lodThreshold")]
    public double LodThreshold { get; set; }

    [JsonPropertyName("parentIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ParentIndex { get; set; }

    [JsonPropertyName("children")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? Children { get; set; }

    [JsonPropertyName("obb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public I3sOrientedBoundingBox? Obb { get; set; }

    [JsonPropertyName("mesh")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public I3sNodeMesh? Mesh { get; set; }
}

public sealed class I3sOrientedBoundingBox
{
    [JsonPropertyName("center")]
    public IReadOnlyList<double> Center { get; set; } = [];

    [JsonPropertyName("halfSize")]
    public IReadOnlyList<double> HalfSize { get; set; } = [];

    [JsonPropertyName("quaternion")]
    public IReadOnlyList<double> Quaternion { get; set; } = [];
}

public sealed class I3sNodeMesh
{
    [JsonPropertyName("geometry")]
    public I3sNodeResourceReference Geometry { get; set; } = new();

    [JsonPropertyName("attribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public I3sNodeResourceReference? Attribute { get; set; }

    [JsonPropertyName("material")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public I3sNodeResourceReference? Material { get; set; }
}

public sealed class I3sNodeResourceReference
{
    [JsonPropertyName("vertexCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? VertexCount { get; set; }

    [JsonPropertyName("featureCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FeatureCount { get; set; }
    [JsonPropertyName("definition")]
    public int Definition { get; set; }

    [JsonPropertyName("resource")]
    public int Resource { get; set; }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(I3sNodePageDocument))]
[JsonSerializable(typeof(I3sNodePageEntry))]
[JsonSerializable(typeof(I3sOrientedBoundingBox))]
[JsonSerializable(typeof(I3sNodeMesh))]
[JsonSerializable(typeof(I3sNodeResourceReference))]
public sealed partial class I3sNodePageJsonContext : JsonSerializerContext
{
}
