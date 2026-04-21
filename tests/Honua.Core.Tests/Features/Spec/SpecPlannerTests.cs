// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
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
        var planner = BuildPlanner();
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
        var planner = BuildPlanner();
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
        var planner = BuildPlanner();
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
        var planner = BuildPlanner();
        var document = Document(
            ComputeNode("a", ("src", "@b")),
            ComputeNode("b", ("src", "@a")));

        var plan = await planner.PlanAsync(document);

        Assert.Empty(plan.Nodes);
        Assert.Contains(plan.Warnings, w => w.Code == SpecDiagnosticCodes.DagCycle);
    }

    [Fact]
    public async Task PlanAsync_ReservedDatasetKind_EmitsSpecKindNotInS1()
    {
        // Dataset/Service/App kinds are reserved for S2. The warning is emitted
        // purely based on kind so DI-registered placeholder stores cannot
        // silently mask the unsupported-in-S1 signal.
        var planner = BuildPlanner();
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

    [Theory]
    [InlineData(SpecResourceKind.Dataset)]
    [InlineData(SpecResourceKind.Service)]
    [InlineData(SpecResourceKind.App)]
    public async Task PlanAsync_AllReservedKinds_EmitSpecKindNotInS1(SpecResourceKind kind)
    {
        var planner = BuildPlanner();
        var document = Document(
            new CanonicalSpecNode
            {
                Id = "n",
                Kind = kind,
                Op = "reserved.op"
            });

        var plan = await planner.PlanAsync(document);

        var n = Assert.Single(plan.Nodes);
        Assert.Contains(n.Warnings, w => w.Code == SpecDiagnosticCodes.SpecKindNotInS1);
    }

    [Fact]
    public async Task PlanAsync_MutableSourceWithoutPin_EmitsMutableSourceNoPin()
    {
        var planner = BuildPlanner();
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
        var planner = BuildPlanner();
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
        var planner = BuildPlanner();
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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PlanAsync_MissingGrammarVersion_EmitsVersionSkewErrorAndDropsNodes(string grammarVersion)
    {
        // Blank grammar/process-family versions would otherwise collide in the
        // content-hash cache key (empty strings hash identically across
        // unrelated specs). Reject at the planner so both REST and gRPC adapters
        // translate to 400 / InvalidArgument before the executor is invoked.
        var planner = BuildPlanner();
        var document = new CanonicalSpecDocument
        {
            GrammarVersion = grammarVersion,
            ProcessFamilyVersion = "family/1.0",
            Nodes = new[] { ComputeNode("a") }
        };

        var plan = await planner.PlanAsync(document);

        Assert.Empty(plan.Nodes);
        var skew = Assert.Single(plan.Warnings, w => w.Code == SpecDiagnosticCodes.VersionSkew);
        Assert.Equal(SpecDiagnosticSeverity.Error, skew.Severity);
        Assert.Contains("grammarVersion", skew.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PlanAsync_MissingProcessFamilyVersion_EmitsVersionSkewErrorAndDropsNodes(string processFamilyVersion)
    {
        var planner = BuildPlanner();
        var document = new CanonicalSpecDocument
        {
            GrammarVersion = "grammar/1.0",
            ProcessFamilyVersion = processFamilyVersion,
            Nodes = new[] { ComputeNode("a") }
        };

        var plan = await planner.PlanAsync(document);

        Assert.Empty(plan.Nodes);
        var skew = Assert.Single(plan.Warnings, w => w.Code == SpecDiagnosticCodes.VersionSkew);
        Assert.Equal(SpecDiagnosticSeverity.Error, skew.Severity);
        Assert.Contains("processFamilyVersion", skew.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanAsync_MissingBothVersions_EmitsSingleAggregateVersionSkew()
    {
        var planner = BuildPlanner();
        var document = new CanonicalSpecDocument
        {
            GrammarVersion = string.Empty,
            ProcessFamilyVersion = string.Empty,
            Nodes = new[] { ComputeNode("a") }
        };

        var plan = await planner.PlanAsync(document);

        Assert.Empty(plan.Nodes);
        var skew = Assert.Single(plan.Warnings, w => w.Code == SpecDiagnosticCodes.VersionSkew);
        Assert.Equal(SpecDiagnosticSeverity.Error, skew.Severity);
        Assert.Contains("grammarVersion", skew.Message, StringComparison.Ordinal);
        Assert.Contains("processFamilyVersion", skew.Message, StringComparison.Ordinal);
    }

    private static SpecPlanner BuildPlanner(
        SpecCostEstimatorOptions? estimatorOptions = null)
    {
        var catalog = new StubProcessCatalog();
        var estimator = new SpecCostEstimator(catalog, estimatorOptions);
        return new SpecPlanner(estimator);
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
