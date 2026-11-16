// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Scene.Assets;

internal interface II3sSceneResourceProvider
{
    Task<I3sSceneResources?> GetResourcesAsync(SceneDataset scene, CancellationToken cancellationToken);
}

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

internal sealed record I3sNodeResources(
    I3sNodeGeometry Geometry,
    IReadOnlyDictionary<string, byte[]> Attributes,
    IReadOnlyDictionary<string, I3sTextureResource> Textures);

internal sealed record I3sNodeGeometry(byte[] Buffer, int VertexCount, int FeatureCount);

internal sealed record I3sTextureResource(byte[] Bytes, string ContentType, string Format);

internal sealed record I3sAssetIdentity(string Path, long Length, DateTime LastWriteUtc);
