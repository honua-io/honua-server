// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Import.Features.I3sImport;

/// <summary>
/// Walks the compact I3S 1.7+ NodePage hierarchy, paging the on-disk
/// <c>nodepages/{page}.json</c> entries on demand so large layers do not have
/// to be fully resident in memory.
/// </summary>
internal sealed class I3sNodeTreeReader
{
    private readonly I3sSlpkReader _slpk;
    private readonly int _nodesPerPage;
    private readonly Dictionary<int, I3sNodePage> _pageCache = [];

    /// <summary>
    /// Initializes a new instance bound to the supplied SLPK reader.
    /// </summary>
    public I3sNodeTreeReader(I3sSlpkReader slpk, int nodesPerPage)
    {
        ArgumentNullException.ThrowIfNull(slpk);
        if (nodesPerPage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nodesPerPage), "nodesPerPage must be positive.");
        }

        _slpk = slpk;
        _nodesPerPage = nodesPerPage;
    }

    /// <summary>
    /// Returns the root node entry (global index 0).
    /// </summary>
    public I3sNodePageEntry GetRoot() => GetNode(0);

    /// <summary>
    /// Resolves a node entry by its global index.
    /// </summary>
    public I3sNodePageEntry GetNode(int index)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Node index cannot be negative.");
        }

        var pageIndex = index / _nodesPerPage;
        if (!_pageCache.TryGetValue(pageIndex, out var page))
        {
            page = LoadPage(pageIndex);
            _pageCache[pageIndex] = page;
        }

        var localIndex = index % _nodesPerPage;
        if (localIndex >= page.Nodes.Length)
        {
            throw new InvalidDataException(
                $"NodePage {pageIndex} does not contain a node at local index {localIndex} (count={page.Nodes.Length}).");
        }

        return page.Nodes[localIndex];
    }

    /// <summary>
    /// Enumerates every node reachable from the root in pre-order traversal.
    /// </summary>
    public IEnumerable<I3sNodePageEntry> EnumerateAllNodes()
    {
        var visited = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            var index = stack.Pop();
            if (!visited.Add(index))
            {
                continue;
            }

            var node = GetNode(index);
            yield return node;

            if (node.Children is null) continue;
            for (var i = node.Children.Length - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }
    }

    private I3sNodePage LoadPage(int pageIndex)
    {
        var path = $"nodepages/{pageIndex}.json";
        if (!_slpk.ContainsEntry(path))
        {
            throw new FileNotFoundException(
                $"I3S .slpk is missing NodePage entry '{path}'.",
                path);
        }

        using var stream = _slpk.OpenEntry(path);
        var page = JsonSerializer.Deserialize(stream, I3sSceneLayerJsonContext.Default.I3sNodePage)
            ?? throw new InvalidDataException($"NodePage '{path}' deserialized to null.");
        return page;
    }
}
