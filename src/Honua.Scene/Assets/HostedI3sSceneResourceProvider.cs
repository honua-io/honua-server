// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;

namespace Honua.Scene.Assets;

/// <summary>Derives every advertised I3S resource from the persisted tileset revision.</summary>
internal sealed class HostedI3sSceneResourceProvider(TimeProvider timeProvider) : II3sSceneResourceProvider, IDisposable
{
    private const long ByteBudget = 64 * 1024 * 1024;
    private const int VertexBudget = 500_000;
    // 3D Tiles SSE = geometricError * focalLength / distance. At 16 pixels,
    // the enclosing sphere projects to pi * (radius * 16 / error)^2 pixels².
    private const double ServingScreenSpaceError = 16;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public async Task<I3sSceneResources?> GetResourcesAsync(SceneDataset scene, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = scene.AssetRoot + "\n" + scene.TilesetFileName;
            var policy = scene.CachePolicy ?? SceneCachePolicy.Default;
            if (!policy.NoStore && _cache.TryGetValue(key, out var existing)
                && timeProvider.GetUtcNow() - existing.Created < TimeSpan.FromSeconds(policy.MaxAgeSeconds)
                && existing.Resources.Assets.All(asset => IsCurrent(scene, asset)))
            {
                return existing.Resources;
            }

            _cache.Remove(key);
            var builder = new ResourceBuilder(scene, cancellationToken);
            var resources = await builder.BuildAsync().ConfigureAwait(false);
            if (!policy.NoStore && policy.MaxAgeSeconds > 0 && resources.ByteSize <= ByteBudget)
            {
                while (_cache.Count >= 16 || _cache.Values.Sum(entry => entry.Resources.ByteSize) + resources.ByteSize > ByteBudget)
                {
                    var oldest = _cache.MinBy(entry => entry.Value.Created).Key;
                    _cache.Remove(oldest);
                }

                _cache[key] = new(timeProvider.GetUtcNow(), resources);
            }

            return resources;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException or IndexOutOfRangeException)
        {
            // Malformed or unsupported source assets are unavailable as a whole;
            // descriptors must never advertise invented or partially decoded resources.
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static bool IsCurrent(SceneDataset scene, I3sAssetIdentity identity)
    {
        var relative = Path.GetRelativePath(scene.AssetRoot, identity.Path).Replace(Path.DirectorySeparatorChar, '/');
        return SceneAssetResolver.TryResolve(scene, relative, out var asset, out _)
            && asset.File.Length == identity.Length && asset.File.LastWriteTimeUtc == identity.LastWriteUtc;
    }

    private sealed record CacheEntry(DateTimeOffset Created, I3sSceneResources Resources);

    private sealed class ResourceBuilder(SceneDataset scene, CancellationToken cancellationToken)
    {
        private readonly List<Node> _nodes = [];
        private readonly List<I3sAssetIdentity> _assets = [];
        private readonly HashSet<string> _activeTilesets = new(StringComparer.Ordinal);
        private long _readBytes;
        private int _vertices;

        public async Task<I3sSceneResources> BuildAsync()
        {
            var rootBytes = await ReadAsync(scene.TilesetFileName).ConfigureAwait(false);
            using var document = JsonDocument.Parse(rootBytes);
            var axis = ReadAxis(document.RootElement);
            var root = document.RootElement.GetProperty("root");
            Walk(root, SceneAffineTransform.Identity, DirectoryOf(scene.TilesetFileName), axis, null, 0, true);
            PruneEmptyNodes();
            var meshes = _nodes.Where(node => node.Mesh is not null).Select(node => node.Mesh!).ToArray();
            if (meshes.Length == 0) { throw new InvalidDataException("Tileset has no persisted mesh content."); }
            var features = meshes.SelectMany(mesh => mesh.Triangles.Select(triangle => mesh.Features[triangle.Feature]))
                .GroupBy(feature => feature.Id).Select(group =>
                {
                    var first = group.First();
                    if (group.Any(feature => feature.Attributes.Count != first.Attributes.Count
                        || feature.Attributes.Any(pair => !first.Attributes.TryGetValue(pair.Key, out var value) || !SameScalar(value, pair.Value))))
                    {
                        throw new InvalidDataException("A feature identity has inconsistent persisted attributes.");
                    }

                    return first;
                }).OrderBy(feature => feature.Id).ToArray();
            var attributes = I3sPersistedResourceEncoder.BuildAttributes(meshes.SelectMany(mesh => mesh.Features).ToArray());
            var materials = new List<I3sMaterialDefinition>();
            var textures = new List<I3sTextureSetDefinition>();
            var resources = new Dictionary<int, I3sNodeResources>();
            for (var index = 0; index < _nodes.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = _nodes[index];
                var positions = DescendantPositions(node.BoundsSource ?? index).ToArray();
                node.Entry.Obb = I3sPersistedResourceEncoder.Bounds(positions);
                node.Entry.LodThreshold = ScreenThreshold(node);
                if (node.Mesh is not { } mesh) { continue; }
                var encoded = I3sPersistedResourceEncoder.Encode(mesh, node.Entry.Obb, attributes, cancellationToken);
                var definition = materials.Count;
                materials.Add(I3sPersistedResourceEncoder.Material(mesh, textures));
                node.Entry.Mesh = new()
                {
                    Geometry = new() { Definition = 0, Resource = index, VertexCount = encoded.Geometry.VertexCount, FeatureCount = encoded.Geometry.FeatureCount },
                    Attribute = new() { Resource = index },
                    Material = new() { Definition = definition, Resource = index },
                };
                resources.Add(index, encoded);
            }

            var all = meshes.SelectMany(mesh => mesh.Triangles.SelectMany(triangle => new[] { triangle.A.Position, triangle.B.Position, triangle.C.Position }))
                .Select(point => EcefCoordinateTransform.FromEcef(point.X, point.Y, point.Z)).ToArray();
            var pages = _nodes.Select(node => node.Entry).Chunk(I3sNodePageProjector.NodesPerPage)
                .Select(entries => new I3sNodePageDocument { Nodes = entries }).ToArray();
            var result = new I3sSceneResources(pages, resources, attributes, I3sPersistedResourceEncoder.BuildFields(attributes), materials, textures,
                I3sPersistedResourceEncoder.Statistics(features, attributes),
                new(all.Min(p => p.Longitude), all.Min(p => p.Latitude), all.Max(p => p.Longitude), all.Max(p => p.Latitude), all.Min(p => p.Height), all.Max(p => p.Height)),
                _assets.ToArray());
            if (result.ByteSize > ByteBudget || !result.Assets.All(asset => IsCurrent(scene, asset)))
            {
                throw new InvalidDataException("Scene revision changed or exceeds the derived resource budget.");
            }

            return result;
        }

        private void Walk(JsonElement tile, double[] parentTransform, string directory, string? upAxis, int? parent, int depth, bool root = false, string inheritedRefine = "REPLACE")
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 128 || _nodes.Count >= 4096) { throw new InvalidDataException("Tileset exceeds the serving node budget."); }
            var transform = SceneAffineTransform.Multiply(parentTransform, SceneAffineTransform.Read(tile, "transform"));
            var error = tile.TryGetProperty("geometricError", out var encodedError) ? encodedError.GetDouble() : 0;
            if (!double.IsFinite(error) || error < 0) { throw new InvalidDataException("Invalid geometric error."); }
            var refine = tile.TryGetProperty("refine", out var encodedRefine) ? encodedRefine.GetString() : inheritedRefine;
            if (refine is not ("ADD" or "REPLACE")) { throw new InvalidDataException("Unsupported tileset refinement mode."); }
            var node = Add(parent, error, null);
            var descendantParent = node;
            if (tile.TryGetProperty("content", out var content))
            {
                var uri = content.TryGetProperty("uri", out var encodedUri) ? encodedUri.GetString() : content.GetProperty("url").GetString();
                var path = Join(directory, uri ?? throw new InvalidDataException("Missing content URI."));
                var bytes = Read(path);
                if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_activeTilesets.Add(path)) { throw new InvalidDataException("Cyclic external tileset."); }
                    using var nested = JsonDocument.Parse(bytes);
                    Walk(nested.RootElement.GetProperty("root"), transform, DirectoryOf(path), ReadAxis(nested.RootElement), node, depth + 1, inheritedRefine: refine);
                    _activeTilesets.Remove(path);
                }
                else
                {
                    var meshes = SceneGlbAssetReader.Read(bytes, transform, upAxis, relative => Read(Join(DirectoryOf(path), relative)), cancellationToken);
                    foreach (var mesh in meshes)
                    {
                        _vertices = checked(_vertices + mesh.Triangles.Count * 3);
                        if (_vertices > VertexBudget) { throw new InvalidDataException("Scene exceeds the serving vertex budget."); }
                    }

                    if (meshes.Count > 1 && refine == "REPLACE"
                        && tile.TryGetProperty("children", out var refinementChildren) && refinementChildren.GetArrayLength() > 0)
                    {
                        throw new InvalidDataException("Multi-material replacement hierarchies require compatible baked LOD meshes.");
                    }

                    if (!root && meshes.Count == 1 && refine == "REPLACE")
                    {
                        _nodes[node].Mesh = meshes[0];
                    }
                    else
                    {
                        // ADD content is a persistent sibling: descendants cannot
                        // replace it. It may become visible earlier than 3D Tiles
                        // SSE traversal, preserving additive content without loss.
                        var groups = meshes.Select(mesh => Add(node, error, mesh)).ToArray();
                        if (refine == "REPLACE")
                        {
                            descendantParent = groups[0];
                            foreach (var group in groups) { _nodes[group].BoundsSource = node; }
                        }
                    }
                }
            }

            if (tile.TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateArray()) { Walk(child, transform, directory, upAxis, descendantParent, depth + 1, inheritedRefine: refine); }
            }
        }

        private int Add(int? parent, double threshold, SceneDecodedMesh? mesh)
        {
            if (_nodes.Count >= 4096) { throw new InvalidDataException("Scene exceeds the serving node budget."); }
            var index = _nodes.Count;
            _nodes.Add(new(new() { Index = index, ParentIndex = parent, Children = new List<int>() }, mesh, threshold));
            if (parent is { } parentIndex) { ((List<int>)_nodes[parentIndex].Entry.Children!).Add(index); }
            return index;
        }

        private IEnumerable<SceneCartesian> DescendantPositions(int index)
        {
            if (_nodes[index].Mesh is { } mesh)
            {
                foreach (var triangle in mesh.Triangles)
                {
                    yield return triangle.A.Position;
                    yield return triangle.B.Position;
                    yield return triangle.C.Position;
                }
            }

            foreach (var child in _nodes[index].Entry.Children!)
            {
                foreach (var position in DescendantPositions(child)) { yield return position; }
            }
        }

        private void PruneEmptyNodes()
        {
            var retained = new bool[_nodes.Count];
            for (var index = _nodes.Count - 1; index >= 0; index--)
            {
                retained[index] = _nodes[index].Mesh is not null || _nodes[index].Entry.Children!.Any(child => retained[child]);
            }

            var mapping = new Dictionary<int, int>();
            for (var index = 0; index < _nodes.Count; index++)
            {
                if (retained[index]) { mapping[index] = mapping.Count; }
            }

            var nodes = _nodes.Where((_, index) => retained[index]).ToArray();
            foreach (var node in nodes)
            {
                node.Entry.Index = mapping[node.Entry.Index];
                node.Entry.ParentIndex = node.Entry.ParentIndex is { } parent ? mapping[parent] : null;
                node.Entry.Children = node.Entry.Children!.Where(child => retained[child]).Select(child => mapping[child]).ToArray();
                node.BoundsSource = node.BoundsSource is { } bounds ? mapping[bounds] : null;
            }

            _nodes.Clear();
            _nodes.AddRange(nodes);
        }

        private async Task<byte[]> ReadAsync(string path)
        {
            var asset = Resolve(path);
            await using var stream = new FileStream(asset.File.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytes = Allocate(stream.Length);
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (stream.Length != bytes.Length) { throw new InvalidDataException("Source asset changed while reading."); }
            Record(asset, bytes.Length);
            return bytes;
        }

        private byte[] Read(string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asset = Resolve(path);
            using var stream = new FileStream(asset.File.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = Allocate(stream.Length);
            stream.ReadExactly(bytes);
            if (stream.Length != bytes.Length) { throw new InvalidDataException("Source asset changed while reading."); }
            Record(asset, bytes.Length);
            return bytes;
        }

        private ResolvedSceneAsset Resolve(string path)
        {
            if (!SceneAssetResolver.TryResolve(scene, path, out var asset, out _) || asset.File.Length > ByteBudget - _readBytes)
            { throw new InvalidDataException("Source asset is unavailable or exceeds the serving byte budget."); }
            return asset;
        }

        private byte[] Allocate(long length)
        {
            if (length < 0 || length > ByteBudget - _readBytes) { throw new InvalidDataException("Source asset exceeds the serving byte budget."); }
            return new byte[checked((int)length)];
        }

        private void Record(ResolvedSceneAsset asset, int bytes)
        {
            _readBytes = checked(_readBytes + bytes);
            if (_readBytes > ByteBudget || bytes != asset.File.Length) { throw new InvalidDataException("Source asset changed while reading."); }
            _assets.Add(new(asset.File.FullName, asset.File.Length, asset.File.LastWriteTimeUtc));
        }

        private static string Join(string directory, string uri) => directory.Length == 0 ? uri : directory + "/" + uri;
        private static string DirectoryOf(string path) => Path.GetDirectoryName(path)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
        private static string? ReadAxis(JsonElement document) => document.TryGetProperty("asset", out var asset) && asset.TryGetProperty("gltfUpAxis", out var axis) ? axis.GetString() : null;
        private static bool SameScalar(object? left, object? right)
        {
            if (Equals(left, right)) { return true; }
            if (left is long && right is double || right is long && left is double)
            {
                var integer = left is long value ? value : (long)right!;
                var number = left is double value2 ? value2 : (double)right!;
                return double.IsFinite(number) && number >= long.MinValue && number < 9223372036854775808d
                    && number == Math.Truncate(number) && (long)number == integer;
            }

            return false;
        }
        private static double ScreenThreshold(Node node)
        {
            if (node.Mesh is null || node.Entry.Children!.Count == 0) { return 0; }
            // Zero source error already meets every finite SSE target. Its mesh
            // must remain selected rather than refining at a threshold of zero.
            if (node.Error == 0) { return double.MaxValue; }
            var radiusSquared = node.Entry.Obb!.HalfSize.Sum(value => value * value);
            var threshold = Math.PI * radiusSquared * ServingScreenSpaceError * ServingScreenSpaceError / (node.Error * node.Error);
            if (!double.IsFinite(threshold) || threshold <= 0) { throw new InvalidDataException("Invalid projected LOD threshold."); }
            return threshold;
        }

        private sealed class Node(I3sNodePageEntry entry, SceneDecodedMesh? mesh, double error)
        {
            public I3sNodePageEntry Entry { get; } = entry;
            public SceneDecodedMesh? Mesh { get; set; } = mesh;
            public double Error { get; } = error;
            public int? BoundsSource { get; set; }
        }
    }
}
