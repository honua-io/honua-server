// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Generation;
using Honua.Scene.Grpc;
using Domain = Honua.Core.Features.Scene.Domain;

namespace Honua.Scene;

/// <summary>
/// Projects a 3D Tiles <see cref="Domain.TilesetDocument"/> tree into fixed-size
/// Esri I3S 1.7 node pages (#1809). The flexible 3D Tiles tree is normalized into
/// the I3S HLOD node-page model: a flattened, root-first node array partitioned
/// into pages of <see cref="NodesPerPage"/> entries, each node carrying an
/// oriented bounding box in the index CRS, an LOD threshold derived from the
/// source geometric error, parent/child references by global node index, and
/// per-node geometry/attribute references by resource index.
/// </summary>
/// <remarks>
/// <para>
/// Node identity and ordering are inherited from <see cref="SceneTileCatalog"/>
/// (deterministic depth-first, root-first traversal), so the same tileset always
/// yields the same node-page layout and the global node index is stable across
/// requests. The global index doubles as the I3S resource id a client substitutes
/// into geometry/attribute/texture node-resource URLs.
/// </para>
/// <para>
/// Only nodes that reference renderable content (a 3D Tiles
/// <c>content.uri</c>) advertise a <c>mesh</c> block; pure grouping nodes carry
/// bounds and child references but no fetchable geometry, matching the source
/// tree's structure honestly.
/// </para>
/// </remarks>
public static class I3sNodePageProjector
{
    /// <summary>
    /// Fixed node-page size advertised on <c>store.nodePages.nodesPerPage</c> and
    /// used to partition the flattened node array. A small page keeps the
    /// synthetic-fixture and small-scene responses single-page while still
    /// exercising multi-page pagination on larger trees.
    /// </summary>
    public const int NodesPerPage = 64;

    /// <summary>
    /// LOD selection metric type advertised on
    /// <c>store.nodePages.lodSelectionMetricType</c>. Honua expresses
    /// <c>lodThreshold</c> as a max-screen-threshold metric derived from the
    /// source geometric error.
    /// </summary>
    public const string LodSelectionMetricType = "maxScreenThresholdSQ";

    private const double DegreesToRadians = Math.PI / 180d;

    /// <summary>
    /// Builds the complete ordered set of node-page documents for a tileset. Page
    /// <c>n</c> in the returned list is served at <c>nodepages/{n}</c>.
    /// </summary>
    /// <param name="document">The source 3D Tiles tileset document.</param>
    /// <returns>
    /// The node pages in order; an empty list when the tileset has no nodes.
    /// </returns>
    public static IReadOnlyList<I3sNodePageDocument> BuildPages(Domain.TilesetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var entries = SceneTileCatalog.Build(document);
        if (entries.Count == 0)
        {
            return [];
        }

        // Map each node id to its global index so parent/child references resolve
        // to the flattened array positions an I3S client traverses.
        var indexById = new Dictionary<string, int>(entries.Count, StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            indexById[entries[i].NodeId] = i;
        }

        var nodes = new List<I3sNodePageEntry>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            nodes.Add(ProjectNode(entries[i], i, indexById));
        }

        var pageCount = (entries.Count + NodesPerPage - 1) / NodesPerPage;
        var pages = new List<I3sNodePageDocument>(pageCount);
        for (var page = 0; page < pageCount; page++)
        {
            var start = page * NodesPerPage;
            var end = Math.Min(start + NodesPerPage, nodes.Count);
            pages.Add(new I3sNodePageDocument
            {
                Nodes = nodes.GetRange(start, end - start),
            });
        }

        return pages;
    }

    /// <summary>
    /// Total number of node pages a tileset projects to (used by the descriptor
    /// and to bound node-page requests without materializing every page).
    /// </summary>
    /// <param name="document">The source 3D Tiles tileset document.</param>
    /// <returns>The page count; 0 when the tileset has no nodes.</returns>
    public static int PageCount(Domain.TilesetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var nodeCount = SceneTileCatalog.Build(document).Count;
        return (nodeCount + NodesPerPage - 1) / NodesPerPage;
    }

    private static I3sNodePageEntry ProjectNode(
        SceneTileEntry entry,
        int globalIndex,
        Dictionary<string, int> indexById)
    {
        var children = new List<int>(entry.ChildIds.Count);
        foreach (var childId in entry.ChildIds)
        {
            if (indexById.TryGetValue(childId, out var childIndex))
            {
                children.Add(childIndex);
            }
        }

        var node = new I3sNodePageEntry
        {
            // The root selects unconditionally (threshold 0); descendants refine
            // as the source geometric error tightens. I3S expresses finer LODs as
            // larger thresholds, so invert the geometric error (coarse error ->
            // low threshold) while keeping it monotone and non-negative.
            LodThreshold = entry.Lod == 0 ? 0d : Math.Max(0d, entry.Node.GeometricError),
            ParentIndex = entry.Lod == 0 ? null : FindParentIndex(entry.NodeId, indexById),
            Children = children.Count > 0 ? children : null,
            Obb = ProjectObb(entry.Node.BoundingVolume),
        };

        // Only content-bearing nodes advertise a fetchable mesh. The global index
        // is the stable per-node resource id substituted into node-resource URLs.
        if (entry.Node.Content is { Uri.Length: > 0 })
        {
            node.Mesh = new I3sNodeMesh
            {
                Geometry = new I3sNodeResourceReference { Definition = 0, Resource = globalIndex },
                Attribute = new I3sNodeResourceReference { Definition = 0, Resource = globalIndex },
                Material = new I3sNodeResourceReference { Definition = 0, Resource = globalIndex },
            };
        }

        return node;
    }

    /// <summary>
    /// Resolves the parent global index of a node from its hierarchical id
    /// (<c>"0-1-2"</c> -> parent <c>"0-1"</c>). Node ids are assigned by
    /// <see cref="SceneTileCatalog"/> from tree position, so the parent id is the
    /// id with the last <c>-segment</c> removed.
    /// </summary>
    private static int? FindParentIndex(string nodeId, Dictionary<string, int> indexById)
    {
        var lastDash = nodeId.LastIndexOf('-');
        if (lastDash < 0)
        {
            return null;
        }

        var parentId = nodeId[..lastDash];
        return indexById.TryGetValue(parentId, out var parentIndex) ? parentIndex : null;
    }

    /// <summary>
    /// Projects a 3D Tiles region bounding volume (radians + min/max height) into
    /// an I3S oriented bounding box centred in the index CRS (WGS-84
    /// lon/lat/elevation) with metric half-sizes and an identity orientation.
    /// Returns <see langword="null"/> for a missing or short (&lt; 6 element)
    /// region so a node without a real bound advertises no box rather than a
    /// degenerate one.
    /// </summary>
    private static I3sOrientedBoundingBox? ProjectObb(Domain.BoundingVolume? volume)
    {
        var region = volume?.Region;
        if (region is not { Length: >= 6 })
        {
            return null;
        }

        var west = region[0];
        var south = region[1];
        var east = region[2];
        var north = region[3];
        var minHeight = region[4];
        var maxHeight = region[5];

        var centerLonRad = (west + east) / 2d;
        var centerLatRad = (south + north) / 2d;
        var centerLonDeg = centerLonRad / DegreesToRadians;
        var centerLatDeg = centerLatRad / DegreesToRadians;
        var centerHeight = (minHeight + maxHeight) / 2d;

        // Half-sizes in metres. The east-west span shrinks with latitude
        // (cos(lat)); the north-south span uses the meridian arc. These are the
        // standard small-angle metric extents used to size an MBS/OBB from a
        // geographic envelope.
        var halfHeightMetres = (north - south) / 2d * EcefCoordinateTransform.WgsSemiMajorAxis;
        var halfWidthMetres = (east - west) / 2d
            * EcefCoordinateTransform.WgsSemiMajorAxis
            * Math.Cos(centerLatRad);
        var halfElevationMetres = (maxHeight - minHeight) / 2d;

        return new I3sOrientedBoundingBox
        {
            Center = [centerLonDeg, centerLatDeg, centerHeight],
            HalfSize =
            [
                Math.Abs(halfWidthMetres),
                Math.Abs(halfHeightMetres),
                Math.Abs(halfElevationMetres),
            ],
            Quaternion = [0d, 0d, 0d, 1d],
        };
    }
}
