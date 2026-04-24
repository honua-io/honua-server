// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Services;

/// <summary>
/// Resolves the dependency graph declared by a <see cref="CanonicalSpecDocument"/>.
/// Produces a topological ordering plus a per-node dependency list, and emits
/// structural diagnostics for cycles, duplicates, and unresolved references.
/// </summary>
internal static class SpecDagResolver
{
    /// <summary>
    /// Returns the resolution result. When <see cref="SpecDagResolution.HasFatalErrors"/>
    /// is true, <see cref="SpecDagResolution.Order"/> is empty and the caller
    /// must surface <see cref="SpecDagResolution.Diagnostics"/> without building a plan.
    /// </summary>
    public static SpecDagResolution Resolve(CanonicalSpecDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var diagnostics = new List<SpecWarning>();
        var nodes = new Dictionary<string, CanonicalSpecNode>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        var hasInvalidId = false;

        foreach (var node in document.Nodes)
        {
            // Blank node ids silently produce blank `nodeId` values in plan and
            // apply output, violating the documented "stable node identifier"
            // contract. Reject before duplicate/dependency analysis so the
            // diagnostic surface is unambiguous — otherwise two blank-id nodes
            // would also trip `duplicate-node-id`, masking the real cause.
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                hasInvalidId = true;
                diagnostics.Add(new SpecWarning
                {
                    Code = SpecDiagnosticCodes.InvalidNodeId,
                    Message = "Node declared without a usable id (null, empty, or whitespace).",
                    Severity = SpecDiagnosticSeverity.Error,
                    Remedy = "Set 'id' to a stable, non-blank identifier unique within the document."
                });
                continue;
            }

            if (!nodes.TryAdd(node.Id, node))
            {
                if (duplicates.Add(node.Id))
                {
                    diagnostics.Add(new SpecWarning
                    {
                        Code = SpecDiagnosticCodes.DuplicateNodeId,
                        Message = $"Node id '{node.Id}' is declared more than once.",
                        Severity = SpecDiagnosticSeverity.Error,
                        NodeId = node.Id,
                        Remedy = "Ensure every spec node has a unique id."
                    });
                }
            }
        }

        // Short-circuit if any node id is invalid — downstream reference
        // resolution would otherwise walk the partial `nodes` map with the
        // survivors and surface misleading unresolved-reference errors.
        if (hasInvalidId)
        {
            return new SpecDagResolution(
                Order: [],
                Dependencies: new Dictionary<string, List<string>>(StringComparer.Ordinal),
                Diagnostics: diagnostics,
                HasFatalErrors: true);
        }

        var dependencies = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in nodes.Values)
        {
            var deps = ExtractDependencies(node, nodes, diagnostics);
            dependencies[node.Id] = deps;
        }

        if (duplicates.Count > 0 || diagnostics.Any(d => d.Code == SpecDiagnosticCodes.UnresolvedReference))
        {
            return new SpecDagResolution(
                Order: [],
                Dependencies: dependencies,
                Diagnostics: diagnostics,
                HasFatalErrors: true);
        }

        if (!TryTopologicalSort(nodes, dependencies, out var order, out var cyclePath))
        {
            diagnostics.Add(new SpecWarning
            {
                Code = SpecDiagnosticCodes.DagCycle,
                Message = $"Spec DAG contains a cycle: {string.Join(" -> ", cyclePath)}.",
                Severity = SpecDiagnosticSeverity.Error,
                Remedy = "Break the cycle by removing or re-binding the offending dependency."
            });
            return new SpecDagResolution(
                Order: [],
                Dependencies: dependencies,
                Diagnostics: diagnostics,
                HasFatalErrors: true);
        }

        return new SpecDagResolution(
            Order: order,
            Dependencies: dependencies,
            Diagnostics: diagnostics,
            HasFatalErrors: false);
    }

    private static List<string> ExtractDependencies(
        CanonicalSpecNode node,
        Dictionary<string, CanonicalSpecNode> nodes,
        List<SpecWarning> diagnostics)
    {
        var deps = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (_, value) in node.Inputs)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            // Canonical reference form: `@node-id` — anything else is a scalar.
            if (value.Length < 2 || value[0] != '@')
            {
                continue;
            }

            var referenced = value[1..];
            if (!seen.Add(referenced))
            {
                continue;
            }

            if (!nodes.ContainsKey(referenced))
            {
                diagnostics.Add(new SpecWarning
                {
                    Code = SpecDiagnosticCodes.UnresolvedReference,
                    Message = $"Node '{node.Id}' references unknown id '{referenced}'.",
                    Severity = SpecDiagnosticSeverity.Error,
                    NodeId = node.Id,
                    Remedy = "Declare the referenced node or fix the reference."
                });
                continue;
            }

            deps.Add(referenced);
        }

        deps.Sort(StringComparer.Ordinal);
        return deps;
    }

    private static bool TryTopologicalSort(
        IReadOnlyDictionary<string, CanonicalSpecNode> nodes,
        IReadOnlyDictionary<string, List<string>> dependencies,
        out IReadOnlyList<string> order,
        out IReadOnlyList<string> cyclePath)
    {
        var inDegree = new Dictionary<string, int>(nodes.Count, StringComparer.Ordinal);
        var reverseEdges = new Dictionary<string, List<string>>(nodes.Count, StringComparer.Ordinal);

        foreach (var id in nodes.Keys)
        {
            inDegree[id] = 0;
            reverseEdges[id] = [];
        }

        foreach (var (from, deps) in dependencies)
        {
            foreach (var dep in deps)
            {
                inDegree[from]++;
                reverseEdges[dep].Add(from);
            }
        }

        // Use a stable order ready queue so identical specs always resolve identically.
        var ready = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (id, degree) in inDegree)
        {
            if (degree == 0)
            {
                ready.Add(id);
            }
        }

        var result = new List<string>(nodes.Count);
        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            result.Add(next);

            foreach (var dependent in reverseEdges[next])
            {
                if (--inDegree[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (result.Count != nodes.Count)
        {
            cyclePath = FindCycle(nodes, dependencies, inDegree);
            order = [];
            return false;
        }

        order = result;
        cyclePath = [];
        return true;
    }

    private static List<string> FindCycle(
        IReadOnlyDictionary<string, CanonicalSpecNode> nodes,
        IReadOnlyDictionary<string, List<string>> dependencies,
        Dictionary<string, int> inDegree)
    {
        var remaining = nodes.Keys.Where(id => inDegree[id] > 0).ToHashSet(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var start in remaining.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (visited.Contains(start))
            {
                continue;
            }

            var stack = new Stack<DfsFrame>();
            var onPath = new HashSet<string>(StringComparer.Ordinal);
            var path = new List<string>();

            stack.Push(new DfsFrame(start, 0));
            onPath.Add(start);
            path.Add(start);

            while (stack.Count > 0)
            {
                var frame = stack.Peek();
                var deps = dependencies[frame.Id];

                if (frame.Index >= deps.Count)
                {
                    stack.Pop();
                    onPath.Remove(frame.Id);
                    path.RemoveAt(path.Count - 1);
                    visited.Add(frame.Id);
                    continue;
                }

                var next = deps[frame.Index];
                stack.Pop();
                stack.Push(frame with { Index = frame.Index + 1 });

                if (!remaining.Contains(next))
                {
                    continue;
                }

                if (onPath.Contains(next))
                {
                    var cycleStart = path.IndexOf(next);
                    var cycle = path.GetRange(cycleStart, path.Count - cycleStart);
                    cycle.Add(next);
                    return cycle;
                }

                if (visited.Contains(next))
                {
                    continue;
                }

                stack.Push(new DfsFrame(next, 0));
                onPath.Add(next);
                path.Add(next);
            }
        }

        // Fallback — return remaining ids so callers still see the scope.
        return remaining.OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    private readonly record struct DfsFrame(string Id, int Index);
}

/// <summary>
/// Result of <see cref="SpecDagResolver.Resolve"/>.
/// </summary>
internal sealed record SpecDagResolution(
    IReadOnlyList<string> Order,
    IReadOnlyDictionary<string, List<string>> Dependencies,
    IReadOnlyList<SpecWarning> Diagnostics,
    bool HasFatalErrors);
