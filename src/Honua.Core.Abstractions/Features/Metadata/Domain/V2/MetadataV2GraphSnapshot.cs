// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Immutable snapshot of a Metadata v2 graph plus pre-built lookup indexes.
/// </summary>
public sealed class MetadataV2GraphSnapshot
{
    /// <summary>
    /// Builds a snapshot from a graph document.
    /// </summary>
    public MetadataV2GraphSnapshot(MetadataV2Graph graph, string etag, DateTimeOffset loadedAt)
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Etag = etag ?? throw new ArgumentNullException(nameof(etag));
        LoadedAt = loadedAt;
        Index = MetadataV2GraphIndex.Build(graph);
    }

    /// <summary>
    /// Underlying graph document.
    /// </summary>
    public MetadataV2Graph Graph { get; }

    /// <summary>
    /// Stable identity tag for cache validation.
    /// </summary>
    public string Etag { get; }

    /// <summary>
    /// Timestamp when this snapshot was loaded into memory.
    /// </summary>
    public DateTimeOffset LoadedAt { get; }

    /// <summary>
    /// Revision of the underlying graph.
    /// </summary>
    public long Revision => Graph.Revision;

    /// <summary>
    /// Pre-built lookup indexes over the graph.
    /// </summary>
    public MetadataV2GraphIndex Index { get; }
}
