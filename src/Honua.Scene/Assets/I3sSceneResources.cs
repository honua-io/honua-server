// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Scene.Assets;

/// <summary>Production serving seam for one consistent persisted scene revision.</summary>
internal interface II3sSceneResourceProvider
{
    Task<I3sSceneResources?> GetResourcesAsync(SceneDataset scene, CancellationToken cancellationToken);
}

/// <summary>Canonical descriptors and bytes derived from the same persisted assets.</summary>
internal sealed record I3sSceneResources(
    IReadOnlyList<I3sNodePageDocument> Pages,
    IReadOnlyDictionary<int, I3sNodeResources> Nodes,
    IReadOnlyList<I3sAttributeStorageInfo> Attributes,
    IReadOnlyList<I3sField> Fields,
    IReadOnlyList<I3sMaterialDefinition> Materials,
    IReadOnlyList<I3sTextureSetDefinition> TextureSets,
    IReadOnlyDictionary<string, I3sAttributeStatisticsDocument> Statistics,
    SceneExtent Extent,
    IReadOnlyList<I3sAssetIdentity> Assets)
{
    public long ByteSize => Nodes.Values.Sum(node => (long)node.Geometry.Buffer.Length
        + node.Attributes.Values.Sum(value => (long)value.Length) + node.Textures.Values.Sum(value => value.Bytes.LongLength));
}

/// <summary>Resources for a content-bearing node in canonical feature order.</summary>
internal sealed record I3sNodeResources(
    I3sNodeGeometry Geometry,
    IReadOnlyDictionary<string, byte[]> Attributes,
    IReadOnlyDictionary<string, I3sTextureResource> Textures);

/// <summary>Uncompressed mesh positions in the published geographic vertex CRS.</summary>
internal sealed record I3sNodeGeometry(byte[] Buffer, int VertexCount, int FeatureCount);

/// <summary>An original embedded or safely resolved texture, without transcoding.</summary>
internal sealed record I3sTextureResource(byte[] Bytes, string ContentType, string Format);

/// <summary>File revision used to invalidate an in-process derived resource entry.</summary>
internal sealed record I3sAssetIdentity(string Path, long Length, DateTime LastWriteUtc);
