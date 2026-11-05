// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed class MetadataV2GraphSnapshot
{
    public MetadataV2GraphSnapshot(MetadataV2Graph graph, string etag, DateTimeOffset loadedAt)
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Etag = etag ?? throw new ArgumentNullException(nameof(etag));
        LoadedAt = loadedAt;
        Index = MetadataV2GraphIndex.Build(graph);
    }

    public MetadataV2Graph Graph { get; }

    public string Etag { get; }

    public DateTimeOffset LoadedAt { get; }

    public long Revision => Graph.Revision;

    public MetadataV2GraphIndex Index { get; }
}
