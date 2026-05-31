// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Import.Features.I3sImport;

/// <summary>
/// Source-generated JSON context for I3S scene-layer descriptor and NodePage
/// payloads consumed by the .slpk → 3D Tiles converter. Keeps the converter
/// AOT-safe by avoiding reflection-based deserialization.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(I3sSceneLayer))]
[JsonSerializable(typeof(I3sSpatialReference))]
[JsonSerializable(typeof(I3sFullExtent))]
[JsonSerializable(typeof(I3sStore))]
[JsonSerializable(typeof(I3sNodePageOptions))]
[JsonSerializable(typeof(I3sGeometrySchema))]
[JsonSerializable(typeof(I3sHeaderField))]
[JsonSerializable(typeof(I3sVertexAttribute))]
[JsonSerializable(typeof(I3sNodePage))]
[JsonSerializable(typeof(I3sNodePageEntry))]
[JsonSerializable(typeof(I3sOrientedBoundingBox))]
[JsonSerializable(typeof(I3sNodeMesh))]
[JsonSerializable(typeof(I3sMeshResourceRef))]
[JsonSerializable(typeof(Dictionary<string, I3sVertexAttribute>))]
internal sealed partial class I3sSceneLayerJsonContext : JsonSerializerContext
{
}
