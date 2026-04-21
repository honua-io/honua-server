// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Services;

namespace Honua.Core.Tests.Features.Spec;

/// <summary>
/// End-to-end behaviour of <see cref="SpecApplyOrchestrator"/>: drives the DAG,
/// honours cache semantics, and emits the contractually-required event stream.
/// These tests validate the acceptance criteria for ticket #789 —
/// cache reuse, closure invalidation, failure isolation, cooperative
/// cancellation, and reserved-kind rejection.
/// </summary>
public class SpecApplyOrchestratorTests
{
    [Fact]
    public async Task Apply_LinearChain_EmitsStartedRunningSucceededPerNodeThenCompleted()
    {
        var fixture = new OrchestratorFixture();
        var document = Document(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")));

        var handle = await fixture.Engine.StartAsync(document, new SpecApplyOptions(), CancellationToken.None);
        var events = await CollectAsync(handle);

        Assert.Equal(2, handle.Plan.Nodes.Count);

        var first = events[0];
        Assert.Equal(SpecApplyEventKind.ApplyStarted, first.Kind);
        Assert.Equal(1, first.Sequence);
        Assert.Equal(handle.ApplyToken, first.ApplyToken);

        var terminal = events[^1];
        Assert.Equal(SpecApplyEventKind.ApplyCompleted, terminal.Kind);
        Assert.NotNull(terminal.Summary);
        Assert.Equal(2, terminal.Summary!.TotalNodes);
        Assert.Equal(2, terminal.Summary.RanNodes);
        Assert.Equal(0, terminal.Summary.CachedNodes);
        Assert.Equal(0, terminal.Summary.FailedNodes);
        Assert.False(terminal.Summary.Cancelled);

        // Each node emits Queued → Running → Succeeded.
        foreach (var nodeId in new[] { "a", "b" })
        {
            var perNode = events.Where(e => e.NodeId == nodeId).Select(e => e.Kind).ToArray();
            Assert.Equal(
                new[] { SpecApplyEventKind.Queued, SpecApplyEventKind.Running, SpecApplyEventKind.Succeeded },
                perNode);
        }

        // Sequence numbers are strictly monotonic.
        for (var i = 1; i < events.Count; i++)
        {
            Assert.True(events[i].Sequence > events[i - 1].Sequence,
                $"Event sequence not monotonic at index {i}");
        }
    }

    [Fact]
    public async Task Apply_SecondRunOnIdenticalDocument_HitsCacheForEveryNode()
    {
        var fixture = new OrchestratorFixture();
        var document = Document(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")),
            ComputeNode("c", ("src", "@b")));

        var first = await CollectAsync(await fixture.Engine.StartAsync(document, new SpecApplyOptions(), CancellationToken.None));
        var firstSummary = first[^1].Summary!;
        Assert.Equal(3, firstSummary.RanNodes);
        Assert.Equal(0, firstSummary.CachedNodes);
        Assert.Equal(3, fixture.Executor.InvocationCount);

        // Rerun: same document, same hashes, the executor must not be invoked again.
        var rerun = await CollectAsync(await fixture.Engine.StartAsync(document, new SpecApplyOptions(), CancellationToken.None));
        var rerunSummary = rerun[^1].Summary!;
        Assert.Equal(0, rerunSummary.RanNodes);
        Assert.Equal(3, rerunSummary.CachedNodes);
        Assert.Equal(3, fixture.Executor.InvocationCount); // unchanged

        // Every per-node terminal event on rerun is Cached (not Succeeded).
        var perNodeTerminal = rerun
            .Where(e => e.NodeId is not null && e.Kind is SpecApplyEventKind.Cached or SpecApplyEventKind.Succeeded)
            .GroupBy(e => e.NodeId!)
            .ToDictionary(g => g.Key, g => g.Last().Kind);
        Assert.Equal(SpecApplyEventKind.Cached, perNodeTerminal["a"]);
        Assert.Equal(SpecApplyEventKind.Cached, perNodeTerminal["b"]);
        Assert.Equal(SpecApplyEventKind.Cached, perNodeTerminal["c"]);
    }

    [Fact]
    public async Task Apply_SingleNodeMutation_ReusesUnchangedAndReRunsClosure()
    {
        var fixture = new OrchestratorFixture();
        var docV1 = Document(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")),
            ComputeNode("c", ("src", "@b")));

        var firstEvents = await CollectAsync(await fixture.Engine.StartAsync(docV1, new SpecApplyOptions(), CancellationToken.None));
        var firstSummary = firstEvents[^1].Summary!;
        Assert.Equal(3, firstSummary.RanNodes);
        Assert.Equal(3, fixture.Executor.InvocationCount);

        var docV2 = Document(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")) with
            {
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["tweak"] = "v2" }
            },
            ComputeNode("c", ("src", "@b")));

        var rerun = await CollectAsync(await fixture.Engine.StartAsync(docV2, new SpecApplyOptions(), CancellationToken.None));
        var rerunSummary = rerun[^1].Summary!;
        Assert.Equal(2, rerunSummary.RanNodes);
        Assert.Equal(1, rerunSummary.CachedNodes);
        Assert.Equal(5, fixture.Executor.InvocationCount); // 3 + b' + c'

        var perNodeTerminal = rerun
            .Where(e => e.NodeId is not null && e.Kind is SpecApplyEventKind.Cached or SpecApplyEventKind.Succeeded)
            .GroupBy(e => e.NodeId!)
            .ToDictionary(g => g.Key, g => g.Last().Kind);
        Assert.Equal(SpecApplyEventKind.Cached, perNodeTerminal["a"]);
        Assert.Equal(SpecApplyEventKind.Succeeded, perNodeTerminal["b"]);
        Assert.Equal(SpecApplyEventKind.Succeeded, perNodeTerminal["c"]);
    }

    [Fact]
    public async Task Apply_CacheBypass_AlwaysInvokesExecutor()
    {
        var fixture = new OrchestratorFixture();
        var document = Document(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")));

        _ = await CollectAsync(await fixture.Engine.StartAsync(document, new SpecApplyOptions(), CancellationToken.None));
        Assert.Equal(2, fixture.Executor.InvocationCount);

        var rerun = await CollectAsync(await fixture.Engine.StartAsync(
            document,
            new SpecApplyOptions { CacheMode = SpecCacheMode.Bypass },
            CancellationToken.None));

        // Every node is re-executed — summary shows ran=2, cached=0.
        var rerunSummary = rerun[^1].Summary!;
        Assert.Equal(2, rerunSummary.RanNodes);
        Assert.Equal(0, rerunSummary.CachedNodes);
        Assert.Equal(4, fixture.Executor.InvocationCount);
    }

    [Fact]
    public async Task Apply_UpstreamFailure_EmitsSkippedWithUpstreamFailed()
    {
        var fixture = new OrchestratorFixture();
        fixture.Executor.FailFor("a", new InvalidOperationException("boom"));

        var document = Document(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")),
            ComputeNode("c", ("src", "@b")));

        var events = await CollectAsync(await fixture.Engine.StartAsync(document, new SpecApplyOptions(), CancellationToken.None));

        var aTerminal = events.Last(e => e.NodeId == "a" && e.Kind is SpecApplyEventKind.Failed);
        Assert.Equal(SpecApplyEventKind.Failed, aTerminal.Kind);
        Assert.NotNull(aTerminal.Diagnostic);
        Assert.Equal(SpecDiagnosticSeverity.Error, aTerminal.Diagnostic!.Severity);

        var bTerminal = Assert.Single(events, e => e.NodeId == "b" && e.Kind == SpecApplyEventKind.Skipped);
        Assert.Equal(SpecDiagnosticCodes.UpstreamFailed, bTerminal.Diagnostic!.Code);

        var cTerminal = Assert.Single(events, e => e.NodeId == "c" && e.Kind == SpecApplyEventKind.Skipped);
        Assert.Equal(SpecDiagnosticCodes.UpstreamFailed, cTerminal.Diagnostic!.Code);

        var summary = events[^1].Summary!;
        Assert.Equal(0, summary.RanNodes);
        Assert.Equal(1, summary.FailedNodes);
        Assert.Equal(2, summary.SkippedNodes);
    }

    [Fact]
    public async Task Apply_ExecutorRaisesSpecExecutionException_PropagatesCodeAndRemedy()
    {
        var fixture = new OrchestratorFixture();
        fixture.Executor.FailFor("a", new SpecExecutionException(
            code: "spec-compute-broken",
            message: "operator not installed",
            nodeId: "a",
            remedy: "install compute.noop"));

        var document = Document(ComputeNode("a"));
        var events = await CollectAsync(await fixture.Engine.StartAsync(document, new SpecApplyOptions(), CancellationToken.None));

        var failed = Assert.Single(events, e => e.Kind == SpecApplyEventKind.Failed);
        Assert.NotNull(failed.Diagnostic);
        Assert.Equal("spec-compute-broken", failed.Diagnostic!.Code);
        Assert.Equal("operator not installed", failed.Diagnostic.Message);
        Assert.Equal("install compute.noop", failed.Diagnostic.Remedy);
    }

    [Fact]
    public async Task Apply_TryCancel_TripsRunAndEmitsApplyCancelled()
    {
        var fixture = new OrchestratorFixture();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        fixture.Executor.HookBeforeExecute("slow", async ct =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
        });

        var document = Document(ComputeNode("slow"));
        var handle = await fixture.Engine.StartAsync(document, new SpecApplyOptions(), CancellationToken.None);

        var collectTask = Task.Run(() => CollectAsync(handle));
        await started.Task;

        Assert.True(fixture.Engine.TryCancel(handle.ApplyToken));

        // The hang resolves via cancellation; release the WaitAsync by tripping the token.
        release.TrySetResult();

        var events = await collectTask;
        var terminal = events[^1];
        Assert.Equal(SpecApplyEventKind.ApplyCancelled, terminal.Kind);
        Assert.True(terminal.Summary!.Cancelled);
    }

    [Fact]
    public async Task TryCancel_UnknownToken_ReturnsFalse()
    {
        var fixture = new OrchestratorFixture();
        Assert.False(fixture.Engine.TryCancel("definitely-not-an-apply-token"));
    }

    [Fact]
    public async Task Apply_ReservedDatasetKind_FailsWithSpecKindNotInS1()
    {
        var fixture = new OrchestratorFixture();
        var document = new CanonicalSpecDocument
        {
            GrammarVersion = "grammar/1.0",
            ProcessFamilyVersion = "family/1.0",
            Nodes = new[]
            {
                new CanonicalSpecNode
                {
                    Id = "d",
                    Kind = SpecResourceKind.Dataset,
                    Op = "dataset.create"
                }
            }
        };

        var events = await CollectAsync(await fixture.Engine.StartAsync(document, new SpecApplyOptions(), CancellationToken.None));

        // Plan stays valid (no store registered, only a warning); apply itself
        // rejects with a Failed event carrying spec-kind-not-in-s1.
        var failed = Assert.Single(events, e => e.Kind == SpecApplyEventKind.Failed);
        Assert.Equal("d", failed.NodeId);
        Assert.Equal(SpecDiagnosticCodes.SpecKindNotInS1, failed.Diagnostic!.Code);
        Assert.Equal(1, events[^1].Summary!.FailedNodes);
        Assert.Equal(0, fixture.Executor.InvocationCount);
    }

    [Fact]
    public async Task Apply_RunWithWarnings_SurfacesThemBeforeSucceeded()
    {
        var fixture = new OrchestratorFixture();
        fixture.Executor.AttachWarnings("a",
            new SpecWarning
            {
                Code = "info-preview",
                Message = "sampled",
                Severity = SpecDiagnosticSeverity.Info
            });

        var events = await CollectAsync(await fixture.Engine.StartAsync(
            Document(ComputeNode("a")),
            new SpecApplyOptions(),
            CancellationToken.None));

        var runningIdx = events.FindIndex(e => e.NodeId == "a" && e.Kind == SpecApplyEventKind.Running);
        var warnIdx = events.FindIndex(e => e.NodeId == "a" && e.Kind == SpecApplyEventKind.Warning);
        var succeededIdx = events.FindIndex(e => e.NodeId == "a" && e.Kind == SpecApplyEventKind.Succeeded);
        Assert.True(runningIdx >= 0 && warnIdx > runningIdx && succeededIdx > warnIdx,
            "Warning must be emitted after Running and before Succeeded.");

        var warning = events[warnIdx].Diagnostic!;
        Assert.Equal("info-preview", warning.Code);
        Assert.Equal(SpecDiagnosticSeverity.Info, warning.Severity);
    }

    // ---- helpers --------------------------------------------------------

    private static async Task<List<SpecApplyEvent>> CollectAsync(SpecApplyHandle handle)
    {
        var list = new List<SpecApplyEvent>();
        await foreach (var evt in handle.Events)
        {
            list.Add(evt);
        }

        return list;
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

    private sealed class OrchestratorFixture
    {
        public OrchestratorFixture()
        {
            var catalog = new StubProcessCatalog();
            var estimator = new SpecCostEstimator(catalog);
            var planner = new SpecPlanner(estimator, Array.Empty<ISpecResourceStateStore>());
            Cache = new InMemoryContentHashArtifactCache();
            Executor = new FakeComputeExecutor();
            Tokens = new SpecApplyTokenRegistry();
            Engine = new SpecApplyOrchestrator(
                planner,
                Executor,
                Cache,
                Tokens,
                Array.Empty<ISpecResourceStateStore>());
        }

        public InMemoryContentHashArtifactCache Cache { get; }

        public FakeComputeExecutor Executor { get; }

        public SpecApplyTokenRegistry Tokens { get; }

        public SpecApplyOrchestrator Engine { get; }
    }

    private sealed class FakeComputeExecutor : ISpecComputeExecutor
    {
        private readonly ConcurrentDictionary<string, Exception> _failures =
            new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, IReadOnlyList<SpecWarning>> _warnings =
            new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, Func<CancellationToken, Task>> _hooks =
            new(StringComparer.Ordinal);

        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public void FailFor(string nodeId, Exception error) => _failures[nodeId] = error;

        public void AttachWarnings(string nodeId, params SpecWarning[] warnings) =>
            _warnings[nodeId] = warnings;

        public void HookBeforeExecute(string nodeId, Func<CancellationToken, Task> hook) =>
            _hooks[nodeId] = hook;

        public async Task<SpecComputeResult> ExecuteAsync(
            CanonicalSpecNode node,
            string contentHash,
            IReadOnlyDictionary<string, CachedArtifactRef> inputs,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);

            if (_hooks.TryGetValue(node.Id, out var hook))
            {
                await hook(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (_failures.TryGetValue(node.Id, out var error))
            {
                throw error;
            }

            var payload = new SpecArtifactPayload
            {
                ContentHash = contentHash,
                Bytes = System.Text.Encoding.UTF8.GetBytes($"result:{node.Id}:{contentHash}"),
                ContentType = "application/json"
            };

            var warnings = _warnings.TryGetValue(node.Id, out var w) ? w : [];

            return new SpecComputeResult
            {
                Payload = payload,
                ActualCost = new SpecCostActual { Rows = 1, Bytes = payload.Bytes.Length, DurationMs = 0 },
                Warnings = warnings
            };
        }
    }

    private sealed class StubProcessCatalog : IProcessCatalog
    {
        public ProcessDefinition? GetProcess(string processId) => null;

        public IReadOnlyList<ProcessDefinition> ListProcesses() => [];

        public IReadOnlyList<ProcessDefinition> GetProcessesByCategory(string category) => [];
    }
}
