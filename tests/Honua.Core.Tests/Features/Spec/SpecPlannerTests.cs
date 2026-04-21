// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Services;

namespace Honua.Core.Tests.Features.Spec;

/// <summary>
/// Covers <see cref="SpecPlanner"/> behaviour: DAG assembly, content-hash
/// assignment, diagnostic propagation, and warning rules.
/// </summary>
public class SpecPlannerTests
{
    [Fact]
    public async Task PlanAsync_LinearChain_AssignsDistinctContentHashes()
    {
        var planner = BuildPlanner(reservedKinds: null);
        var document = Document(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")));

        var plan = await planner.PlanAsync(document);

        Assert.Equal(2, plan.Nodes.Count);
        Assert.NotEqual(plan.Nodes[0].ContentHash, plan.Nodes[1].ContentHash);
        Assert.Equal("a", plan.Nodes[0].NodeId);
        Assert.Equal("b", plan.Nodes[1].NodeId);
        Assert.Equal(new[] { "a" }, plan.Nodes[1].DependsOn);
    }

    [Fact]
    public async Task PlanAsync_SameDocumentTwice_ProducesSameContentHashes()
    {
        var planner = BuildPlanner(reservedKinds: null);
        var document = Document(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")),
            ComputeNode("c", ("src", "@b")));

        var plan1 = await planner.PlanAsync(document);
        var plan2 = await planner.PlanAsync(document);

        Assert.Equal(plan1.Nodes.Count, plan2.Nodes.Count);
        for (var i = 0; i < plan1.Nodes.Count; i++)
        {
            Assert.Equal(plan1.Nodes[i].NodeId, plan2.Nodes[i].NodeId);
            Assert.Equal(plan1.Nodes[i].ContentHash, plan2.Nodes[i].ContentHash);
        }
    }

    [Fact]
    public async Task PlanAsync_SingleNodeMutation_InvalidatesClosureOnly()
    {
        var planner = BuildPlanner(reservedKinds: null);
        var docV1 = Document(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")),
            ComputeNode("c", ("src", "@b")));

        // Mutate only node 'b' — the content change must invalidate b AND c,
        // but not a. That proves cache closure semantics.
        var docV2 = Document(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")) with
            {
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["changed"] = "yes" }
            },
            ComputeNode("c", ("src", "@b")));

        var plan1 = await planner.PlanAsync(docV1);
        var plan2 = await planner.PlanAsync(docV2);

        var hashes1 = plan1.Nodes.ToDictionary(n => n.NodeId, n => n.ContentHash);
        var hashes2 = plan2.Nodes.ToDictionary(n => n.NodeId, n => n.ContentHash);

        Assert.Equal(hashes1["a"], hashes2["a"]);
        Assert.NotEqual(hashes1["b"], hashes2["b"]);
        Assert.NotEqual(hashes1["c"], hashes2["c"]);
    }

    [Fact]
    public async Task PlanAsync_WithCycle_ReturnsEmptyNodesAndCycleWarning()
    {
        var planner = BuildPlanner(reservedKinds: null);
        var document = Document(
            ComputeNode("a", ("src", "@b")),
            ComputeNode("b", ("src", "@a")));

        var plan = await planner.PlanAsync(document);

        Assert.Empty(plan.Nodes);
        Assert.Contains(plan.Warnings, w => w.Code == SpecDiagnosticCodes.DagCycle);
    }

    [Fact]
    public async Task PlanAsync_ReservedKindWithoutStore_EmitsSpecKindNotInS1()
    {
        // No resource state store registered for Dataset — the planner surfaces
        // spec-kind-not-in-s1 so the operator sees the reason up-front.
        var planner = BuildPlanner(reservedKinds: null);
        var document = Document(
            new CanonicalSpecNode
            {
                Id = "d",
                Kind = SpecResourceKind.Dataset,
                Op = "dataset.create"
            });

        var plan = await planner.PlanAsync(document);

        var d = Assert.Single(plan.Nodes);
        Assert.Equal("d", d.NodeId);
        Assert.Contains(d.Warnings, w => w.Code == SpecDiagnosticCodes.SpecKindNotInS1);
    }

    [Fact]
    public async Task PlanAsync_ReservedKindWithStore_SkipsSpecKindWarning()
    {
        // When a store is registered (even the reserved S1 one), planner does
        // not emit spec-kind-not-in-s1 — apply rejects at orchestration time.
        var planner = BuildPlanner(reservedKinds: new[] { SpecResourceKind.Dataset });
        var document = Document(
            new CanonicalSpecNode
            {
                Id = "d",
                Kind = SpecResourceKind.Dataset,
                Op = "dataset.create"
            });

        var plan = await planner.PlanAsync(document);

        var d = Assert.Single(plan.Nodes);
        Assert.DoesNotContain(d.Warnings, w => w.Code == SpecDiagnosticCodes.SpecKindNotInS1);
    }

    [Fact]
    public async Task PlanAsync_MutableSourceWithoutPin_EmitsMutableSourceNoPin()
    {
        var planner = BuildPlanner(reservedKinds: null);
        var document = Document(
            ComputeNode("a") with
            {
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source.mutable"] = "true"
                }
            });

        var plan = await planner.PlanAsync(document);

        var a = Assert.Single(plan.Nodes);
        Assert.Contains(a.Warnings, w => w.Code == SpecDiagnosticCodes.MutableSourceNoPin);
    }

    [Fact]
    public async Task PlanAsync_MutableSourceWithPin_SuppressesWarning()
    {
        var planner = BuildPlanner(reservedKinds: null);
        var document = Document(
            ComputeNode("a") with
            {
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source.mutable"] = "true"
                },
                SourcePins = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["roads"] = "v1"
                }
            });

        var plan = await planner.PlanAsync(document);

        var a = Assert.Single(plan.Nodes);
        Assert.DoesNotContain(a.Warnings, w => w.Code == SpecDiagnosticCodes.MutableSourceNoPin);
    }

    [Fact]
    public async Task PlanAsync_NondeterministicOp_EmitsInfoWarning()
    {
        var planner = BuildPlanner(reservedKinds: null);
        var document = Document(
            ComputeNode("a") with { Nondeterministic = true });

        var plan = await planner.PlanAsync(document);

        var a = Assert.Single(plan.Nodes);
        var warn = Assert.Single(a.Warnings, w => w.Code == SpecDiagnosticCodes.NondeterministicOp);
        Assert.Equal(SpecDiagnosticSeverity.Info, warn.Severity);
    }

    [Fact]
    public async Task PlanAsync_OversizeEstimate_EmitsEstimatedOversizeWarning()
    {
        var planner = BuildPlanner(
            reservedKinds: null,
            estimatorOptions: new SpecCostEstimatorOptions
            {
                DefaultRowsWhenUnknown = 10_000_000L, // default bytes ~5GiB, triggers oversize
                OversizeThresholdBytes = 1L * 1024 * 1024 * 1024
            });

        var document = Document(ComputeNode("big"));

        var plan = await planner.PlanAsync(document);

        var n = Assert.Single(plan.Nodes);
        Assert.Contains(n.Warnings, w => w.Code == SpecDiagnosticCodes.EstimatedOversize);
        Assert.NotNull(n.Cost.EstimatedBytes);
        Assert.True(n.Cost.EstimatedBytes > 1024L * 1024 * 1024);
    }

    private static SpecPlanner BuildPlanner(
        SpecResourceKind[]? reservedKinds,
        SpecCostEstimatorOptions? estimatorOptions = null)
    {
        var catalog = new StubProcessCatalog();
        var estimator = new SpecCostEstimator(catalog, estimatorOptions);
        var stores = (reservedKinds ?? [])
            .Select(k => (ISpecResourceStateStore)new ReservedSpecResourceStateStore(k))
            .ToArray();
        return new SpecPlanner(estimator, stores);
    }

    private static CanonicalSpecDocument Document(params CanonicalSpecNode[] nodes) => new()
    {
        GrammarVersion = "grammar/1.0",
        ProcessFamilyVersion = "family/1.0",
        Nodes = nodes
    };

    private static CanonicalSpecNode ComputeNode(string id, params (string Key, string Value)[] inputs)
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

    private sealed class StubProcessCatalog : IProcessCatalog
    {
        public ProcessDefinition? GetProcess(string processId) => null;

        public IReadOnlyList<ProcessDefinition> ListProcesses() => [];

        public IReadOnlyList<ProcessDefinition> GetProcessesByCategory(string category) => [];
    }
}
