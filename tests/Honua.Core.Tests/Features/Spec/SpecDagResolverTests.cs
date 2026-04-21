// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Services;

namespace Honua.Core.Tests.Features.Spec;

/// <summary>
/// Covers the structural diagnostics surfaced by <see cref="SpecDagResolver"/>:
/// topological ordering, duplicate ids, unresolved references, and cycle
/// detection.
/// </summary>
public class SpecDagResolverTests
{
    [Fact]
    public void Resolve_LinearChain_ReturnsTopologicalOrder()
    {
        // a -> b -> c
        var document = Document(
            Node("a"),
            Node("b", ("src", "@a")),
            Node("c", ("src", "@b")));

        var resolution = SpecDagResolver.Resolve(document);

        Assert.False(resolution.HasFatalErrors);
        Assert.Empty(resolution.Diagnostics);
        Assert.Equal(new[] { "a", "b", "c" }, resolution.Order);
        Assert.Equal(new[] { "a" }, resolution.Dependencies["b"]);
        Assert.Equal(new[] { "b" }, resolution.Dependencies["c"]);
        Assert.Empty(resolution.Dependencies["a"]);
    }

    [Fact]
    public void Resolve_Diamond_ResolvesWithStableOrder()
    {
        //   a
        //  / \
        // b   c
        //  \ /
        //   d
        var document = Document(
            Node("a"),
            Node("b", ("src", "@a")),
            Node("c", ("src", "@a")),
            Node("d", ("left", "@b"), ("right", "@c")));

        var resolution = SpecDagResolver.Resolve(document);

        Assert.False(resolution.HasFatalErrors);
        Assert.Equal(4, resolution.Order.Count);
        Assert.Equal("a", resolution.Order[0]);
        Assert.Equal("d", resolution.Order[^1]);
        // SortedSet stability: with two 0-degree-ready nodes, alphabetical wins.
        Assert.Equal("b", resolution.Order[1]);
        Assert.Equal("c", resolution.Order[2]);
    }

    [Fact]
    public void Resolve_SameDocumentTwice_ProducesIdenticalOrder()
    {
        var document = Document(
            Node("x", ("src", "@y")),
            Node("y"),
            Node("z", ("src", "@x")));

        var first = SpecDagResolver.Resolve(document);
        var second = SpecDagResolver.Resolve(document);

        Assert.Equal(first.Order, second.Order);
    }

    [Fact]
    public void Resolve_DuplicateNodeId_EmitsErrorAndBlocks()
    {
        var document = Document(
            Node("a"),
            Node("a", ("op", "duplicate")));

        var resolution = SpecDagResolver.Resolve(document);

        Assert.True(resolution.HasFatalErrors);
        Assert.Empty(resolution.Order);
        var diag = Assert.Single(resolution.Diagnostics);
        Assert.Equal(SpecDiagnosticCodes.DuplicateNodeId, diag.Code);
        Assert.Equal(SpecDiagnosticSeverity.Error, diag.Severity);
        Assert.Equal("a", diag.NodeId);
    }

    [Fact]
    public void Resolve_UnresolvedReference_EmitsError()
    {
        var document = Document(
            Node("a", ("src", "@missing")));

        var resolution = SpecDagResolver.Resolve(document);

        Assert.True(resolution.HasFatalErrors);
        var diag = Assert.Single(resolution.Diagnostics);
        Assert.Equal(SpecDiagnosticCodes.UnresolvedReference, diag.Code);
        Assert.Equal(SpecDiagnosticSeverity.Error, diag.Severity);
        Assert.Equal("a", diag.NodeId);
        Assert.Contains("missing", diag.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_SelfCycle_EmitsDagCycle()
    {
        var document = Document(
            Node("a", ("src", "@a")));

        var resolution = SpecDagResolver.Resolve(document);

        Assert.True(resolution.HasFatalErrors);
        var diag = Assert.Single(resolution.Diagnostics);
        Assert.Equal(SpecDiagnosticCodes.DagCycle, diag.Code);
        Assert.Contains("a -> a", diag.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_TwoNodeCycle_EmitsDagCycle()
    {
        var document = Document(
            Node("a", ("src", "@b")),
            Node("b", ("src", "@a")));

        var resolution = SpecDagResolver.Resolve(document);

        Assert.True(resolution.HasFatalErrors);
        var diag = Assert.Single(resolution.Diagnostics);
        Assert.Equal(SpecDiagnosticCodes.DagCycle, diag.Code);
    }

    [Fact]
    public void Resolve_ScalarInputs_AreNotTreatedAsReferences()
    {
        var document = Document(
            Node("a", ("distance", "100"), ("crs", "epsg:4326")));

        var resolution = SpecDagResolver.Resolve(document);

        Assert.False(resolution.HasFatalErrors);
        Assert.Empty(resolution.Diagnostics);
        Assert.Empty(resolution.Dependencies["a"]);
    }

    [Fact]
    public void Resolve_DuplicateReference_DeduplicatesDependencies()
    {
        var document = Document(
            Node("src"),
            Node("a", ("left", "@src"), ("right", "@src")));

        var resolution = SpecDagResolver.Resolve(document);

        Assert.False(resolution.HasFatalErrors);
        Assert.Equal(new[] { "src" }, resolution.Dependencies["a"]);
    }

    private static CanonicalSpecDocument Document(params CanonicalSpecNode[] nodes) => new()
    {
        GrammarVersion = "grammar/1.0",
        ProcessFamilyVersion = "family/1.0",
        Nodes = nodes
    };

    private static CanonicalSpecNode Node(string id, params (string Key, string Value)[] inputs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in inputs)
        {
            map[k] = v;
        }

        return new CanonicalSpecNode
        {
            Id = id,
            Kind = SpecResourceKind.Compute,
            Op = "compute.noop",
            Inputs = map
        };
    }
}
