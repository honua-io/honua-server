// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
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

        // Plan emits the warning unconditionally for reserved kinds; apply
        // itself rejects with a Failed event carrying spec-kind-not-in-s1 so
        // DI-registered placeholder stores cannot mask the S1 gap.
        var failed = Assert.Single(events, e => e.Kind == SpecApplyEventKind.Failed);
        Assert.Equal("d", failed.NodeId);
        Assert.Equal(SpecDiagnosticCodes.SpecKindNotInS1, failed.Diagnostic!.Code);
        Assert.Equal(1, events[^1].Summary!.FailedNodes);
        Assert.Equal(0, fixture.Executor.InvocationCount);
    }

    [Fact]
    public async Task StartAsync_DuplicateNodeIds_ThrowsSpecDocumentInvalidException()
    {
        var fixture = new OrchestratorFixture();
        var document = new CanonicalSpecDocument
        {
            GrammarVersion = "grammar/1.0",
            ProcessFamilyVersion = "family/1.0",
            Nodes = new[]
            {
                new CanonicalSpecNode { Id = "a", Kind = SpecResourceKind.Compute, Op = "compute.noop" },
                new CanonicalSpecNode { Id = "a", Kind = SpecResourceKind.Compute, Op = "compute.noop" }
            }
        };

        var ex = await Assert.ThrowsAsync<SpecDocumentInvalidException>(
            () => fixture.Engine.StartAsync(document, new SpecApplyOptions(), CancellationToken.None));
        Assert.Equal(SpecDiagnosticCodes.DuplicateNodeId, ex.PrimaryDiagnostic.Code);
        Assert.Equal(0, fixture.Executor.InvocationCount);
    }

    [Fact]
    public async Task StartAsync_Cycle_ThrowsSpecDocumentInvalidException()
    {
        var fixture = new OrchestratorFixture();
        var document = Document(
            ComputeNode("a", ("src", "@b")),
            ComputeNode("b", ("src", "@a")));

        var ex = await Assert.ThrowsAsync<SpecDocumentInvalidException>(
            () => fixture.Engine.StartAsync(document, new SpecApplyOptions(), CancellationToken.None));
        Assert.Equal(SpecDiagnosticCodes.DagCycle, ex.PrimaryDiagnostic.Code);
        Assert.Equal(0, fixture.Executor.InvocationCount);
    }

    [Fact]
    public async Task StartAsync_UnresolvedReference_ThrowsSpecDocumentInvalidException()
    {
        var fixture = new OrchestratorFixture();
        var document = Document(
            ComputeNode("a", ("src", "@missing")));

        var ex = await Assert.ThrowsAsync<SpecDocumentInvalidException>(
            () => fixture.Engine.StartAsync(document, new SpecApplyOptions(), CancellationToken.None));
        Assert.Equal(SpecDiagnosticCodes.UnresolvedReference, ex.PrimaryDiagnostic.Code);
        Assert.Equal(0, fixture.Executor.InvocationCount);
    }

    [Fact]
    public async Task Apply_EnumeratorCancellationDuringQuietPeriod_DetachesStreamImmediately()
    {
        // Regression for the review finding: ReadEventsAsync used to await
        // WaitToReadAsync without honouring the enumerator token, so a caller
        // that cancelled during a long quiet period stayed attached until the
        // next event or the terminal frame fired. The iterator now threads the
        // [EnumeratorCancellation] token through WaitToReadAsync, so
        // `WithCancellation(...)` on both REST (RequestAborted) and gRPC
        // (context.CancellationToken) detaches immediately. The orchestrator
        // CTS is not touched — the background run must continue.
        var fixture = new OrchestratorFixture();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        fixture.Executor.HookBeforeExecute("slow", async ct =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
        });

        var handle = await fixture.Engine.StartAsync(
            Document(ComputeNode("slow")),
            new SpecApplyOptions(),
            CancellationToken.None);

        using var enumeratorCts = new CancellationTokenSource();

        await started.Task;

        // Drive the enumerator until it blocks: drain any frames already in
        // the channel, then race MoveNextAsync against a small delay. The node
        // is parked in its hook, so once the initial frames (ApplyStarted,
        // Queued, Running) drain, the iterator awaits WaitToReadAsync. If the
        // enumerator token is not honoured, MoveNextAsync hangs past the delay
        // and the assertion below fails; with the fix in place, cancelling
        // trips WaitToReadAsync and MoveNextAsync throws OCE promptly.
        var enumerator = handle.Events.GetAsyncEnumerator(enumeratorCts.Token);
        var observed = new List<SpecApplyEvent>();
        try
        {
            while (true)
            {
                var moveTask = enumerator.MoveNextAsync().AsTask();
                var drainTimeout = Task.Delay(200);
                var winner = await Task.WhenAny(moveTask, drainTimeout);
                if (winner == drainTimeout)
                {
                    // Iterator is parked on WaitToReadAsync. Cancel and verify
                    // detachment is prompt rather than waiting for the next
                    // event. A 2s budget is generous for a local WaitToReadAsync
                    // trip; without the fix the task would hang indefinitely.
                    enumeratorCts.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        async () => await moveTask.WaitAsync(TimeSpan.FromSeconds(2)));
                    break;
                }

                if (!await moveTask)
                {
                    Assert.Fail("Stream terminated before enumerator could be quiesced.");
                }

                observed.Add(enumerator.Current);
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        Assert.Contains(observed, e => e.Kind == SpecApplyEventKind.ApplyStarted);

        // The background run is intentionally decoupled from the caller — it
        // must still reach a terminal state after we let the hook finish. We
        // wait on the token registry because TryCancel/completion both release
        // the token; neither depends on the enumerator being alive.
        release.TrySetResult();

        using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (fixture.Tokens.Contains(handle.ApplyToken))
        {
            await Task.Delay(20, drainCts.Token);
        }

        Assert.Equal(1, fixture.Executor.InvocationCount);
    }

    [Fact]
    public async Task Apply_CallerCancellation_DoesNotStopRun()
    {
        // Regression test for the review finding: SSE/gRPC stream disconnects
        // must not cancel the apply run. The caller-supplied token only scopes
        // the initial plan phase — once StartAsync returns, the run is owned
        // by the orchestrator CTS and only TryCancel can trip it.
        var fixture = new OrchestratorFixture();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        fixture.Executor.HookBeforeExecute("slow", async ct =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
        });

        var document = Document(ComputeNode("slow"));

        using var callerCts = new CancellationTokenSource();
        var handle = await fixture.Engine.StartAsync(document, new SpecApplyOptions(), callerCts.Token);

        var collectTask = Task.Run(() => CollectAsync(handle));
        await started.Task;

        // Simulate the client disconnecting / request-aborted token firing.
        callerCts.Cancel();

        // The run keeps going; allow the hook to complete naturally.
        release.TrySetResult();

        var events = await collectTask;
        var terminal = events[^1];
        Assert.Equal(SpecApplyEventKind.ApplyCompleted, terminal.Kind);
        Assert.False(terminal.Summary!.Cancelled);
        Assert.Equal(1, terminal.Summary.RanNodes);
    }

    [Fact]
    public async Task Apply_TryCancel_DeferredNodesEmitApplyCancelledNotUpstreamFailed()
    {
        // Regression: the earlier skip path emitted `upstream-failed` for every
        // downstream node when the real cause was cooperative cancellation.
        // Admin tooling keys off the diagnostic code — confusing the two codes
        // leads operators to hunt for a phantom upstream failure.
        var fixture = new OrchestratorFixture();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        fixture.Executor.HookBeforeExecute("slow", async ct =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
        });

        var document = Document(
            ComputeNode("slow"),
            ComputeNode("downstream", ("src", "@slow")));

        var handle = await fixture.Engine.StartAsync(document, new SpecApplyOptions { MaxConcurrency = 1 }, CancellationToken.None);
        var collectTask = Task.Run(() => CollectAsync(handle));
        await started.Task;

        Assert.True(fixture.Engine.TryCancel(handle.ApplyToken));
        release.TrySetResult();

        var events = await collectTask;

        // The cancelled executor raises OperationCanceledException, which
        // emits Skipped+apply-cancelled for the node that was running, and the
        // downstream frontier should carry the same diagnostic (not
        // upstream-failed) because cancellation is the root cause.
        var perNodeSkipped = events
            .Where(e => e.Kind == SpecApplyEventKind.Skipped)
            .ToList();
        Assert.NotEmpty(perNodeSkipped);
        Assert.All(perNodeSkipped, evt =>
        {
            Assert.NotNull(evt.Diagnostic);
            Assert.Equal(SpecDiagnosticCodes.ApplyCancelled, evt.Diagnostic!.Code);
        });

        Assert.Equal(SpecApplyEventKind.ApplyCancelled, events[^1].Kind);
        Assert.True(events[^1].Summary!.Cancelled);
    }

    [Fact]
    public async Task Apply_MutableSourceWithoutPin_StampsTtlOnCachedPayload()
    {
        // Ticket #789 contract: nodes the planner flags with
        // `mutable-source-no-pin` must degrade via TTL. The orchestrator stamps
        // the configured TTL onto the payload before PutAsync when the executor
        // returned no TTL of its own; without it the entry would live forever
        // and silently diverge from the mutable upstream.
        var fixture = new OrchestratorFixture();
        var ttl = TimeSpan.FromMinutes(3);

        var document = Document(
            ComputeNode("m") with
            {
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source.mutable"] = "true"
                }
            });

        var events = await CollectAsync(await fixture.Engine.StartAsync(
            document,
            new SpecApplyOptions { MutableSourceTtl = ttl },
            CancellationToken.None));

        var succeeded = Assert.Single(events, e => e.Kind == SpecApplyEventKind.Succeeded);
        var contentHash = succeeded.ContentHash!;
        var stamped = Assert.Single(fixture.Cache.PutPayloads, p => p.ContentHash == contentHash);
        Assert.NotNull(stamped.Ttl);
        Assert.Equal(ttl, stamped.Ttl);
    }

    [Fact]
    public async Task Apply_NonMutableNode_DoesNotStampTtl()
    {
        // Guardrail: TTL stamping must be scoped to nodes the planner flagged
        // as mutable-source-no-pin. Plain nodes keep their indefinite cache
        // lifetime so idempotent reruns stay on the cached path.
        var fixture = new OrchestratorFixture();

        var events = await CollectAsync(await fixture.Engine.StartAsync(
            Document(ComputeNode("plain")),
            new SpecApplyOptions { MutableSourceTtl = TimeSpan.FromMinutes(3) },
            CancellationToken.None));

        var succeeded = Assert.Single(events, e => e.Kind == SpecApplyEventKind.Succeeded);
        var contentHash = succeeded.ContentHash!;
        var put = Assert.Single(fixture.Cache.PutPayloads, p => p.ContentHash == contentHash);
        Assert.Null(put.Ttl);
    }

    [Fact]
    public async Task Apply_ReadOnlyCacheMiss_EmitsFailedWithReadOnlyCacheMissAndSkipsExecutor()
    {
        // ReadOnly is a warm-cache dry run. A miss must NOT invoke the
        // executor and must NOT synthesise a Succeeded event with a hash that
        // is not in the cache — that would break the
        // `GET /v1/spec/artifact/{hash}` contract.
        var fixture = new OrchestratorFixture();

        var events = await CollectAsync(await fixture.Engine.StartAsync(
            Document(ComputeNode("cold")),
            new SpecApplyOptions { CacheMode = SpecCacheMode.ReadOnly },
            CancellationToken.None));

        Assert.Equal(0, fixture.Executor.InvocationCount);
        var failed = Assert.Single(events, e => e.Kind == SpecApplyEventKind.Failed);
        Assert.Equal("cold", failed.NodeId);
        Assert.Equal(SpecDiagnosticCodes.ReadOnlyCacheMiss, failed.Diagnostic!.Code);
        Assert.Equal(SpecDiagnosticSeverity.Error, failed.Diagnostic.Severity);

        // ApplyCompleted still terminates the run; Cancelled stays false since
        // the failure was organic, not a cancel.
        var terminal = events[^1];
        Assert.Equal(SpecApplyEventKind.ApplyCompleted, terminal.Kind);
        Assert.False(terminal.Summary!.Cancelled);
        Assert.Equal(1, terminal.Summary.FailedNodes);
    }

    [Fact]
    public async Task Apply_ReadOnlyCacheHit_ServesFromCacheWithoutInvokingExecutor()
    {
        // Complements the miss test: if the cache is warm, ReadOnly completes
        // with Cached events for every node (the original contract for the
        // "dry run against a warm cache" workflow).
        var fixture = new OrchestratorFixture();
        var document = Document(ComputeNode("warm"));

        _ = await CollectAsync(await fixture.Engine.StartAsync(document, new SpecApplyOptions(), CancellationToken.None));
        Assert.Equal(1, fixture.Executor.InvocationCount);

        var rerun = await CollectAsync(await fixture.Engine.StartAsync(
            document,
            new SpecApplyOptions { CacheMode = SpecCacheMode.ReadOnly },
            CancellationToken.None));

        Assert.Equal(1, fixture.Executor.InvocationCount); // unchanged
        var cached = Assert.Single(rerun, e => e.Kind == SpecApplyEventKind.Cached);
        Assert.Equal("warm", cached.NodeId);
        Assert.DoesNotContain(rerun, e => e.Kind == SpecApplyEventKind.Failed);
    }

    [Fact]
    public async Task Apply_CancelWhileSiblingBlockedOnSemaphore_EmitsTerminalApplyCancelled()
    {
        // Regression for the early-cancellation invariant: a sibling waiting on
        // the MaxConcurrency gate (pre-executor) must not escape the apply with
        // an OperationCanceledException when the token trips. Whichever node
        // wins the gate race is held in its hook; the loser is blocked at
        // gate.WaitAsync. Cancelling must close the stream with ApplyCancelled
        // and the blocked sibling must be emitted as Skipped — not rogue
        // Succeeded, not OCE-escape that skips the terminal frame.
        var fixture = new OrchestratorFixture();
        var aStarted = new TaskCompletionSource();
        var bStarted = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        fixture.Executor.HookBeforeExecute("a", async ct =>
        {
            aStarted.TrySetResult();
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
        });
        fixture.Executor.HookBeforeExecute("b", async ct =>
        {
            bStarted.TrySetResult();
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
        });

        var document = Document(
            ComputeNode("a"),
            ComputeNode("b"));

        var handle = await fixture.Engine.StartAsync(
            document,
            new SpecApplyOptions { MaxConcurrency = 1 },
            CancellationToken.None);
        var collectTask = Task.Run(() => CollectAsync(handle));

        // Only one node can have entered its hook under MaxConcurrency=1. The
        // other sibling must be blocked at gate.WaitAsync by design.
        var first = await Task.WhenAny(aStarted.Task, bStarted.Task);
        await first;

        Assert.True(fixture.Engine.TryCancel(handle.ApplyToken));
        release.TrySetResult();

        var events = await collectTask;
        var terminal = events[^1];
        Assert.Equal(SpecApplyEventKind.ApplyCancelled, terminal.Kind);
        Assert.True(terminal.Summary!.Cancelled);

        // No node may have reached Succeeded — the running one was cancelled
        // inside its hook, and the gate-blocked sibling never reached the
        // executor. Both must be accounted for by a Skipped event.
        Assert.DoesNotContain(events, e => e.Kind == SpecApplyEventKind.Succeeded);
        var skipped = events.Where(e => e.Kind == SpecApplyEventKind.Skipped).ToList();
        Assert.Equal(2, skipped.Count);
        Assert.All(skipped, evt =>
        {
            Assert.NotNull(evt.Diagnostic);
            Assert.Equal(SpecDiagnosticCodes.ApplyCancelled, evt.Diagnostic!.Code);
        });
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

    [Fact]
    public async Task Apply_ExecutorFailure_RecordsNodeDurationWithFailedOutcome()
    {
        // Regression: failed nodes used to bypass NodeDurationMs entirely, so
        // the histogram only carried successful tails. Dashboards keying off
        // per-node duration silently under-counted the failure path, which
        // also made slow-failing executors invisible on alerting.
        var fixture = new OrchestratorFixture();
        fixture.Executor.FailFor("a", new InvalidOperationException("boom"));

        var samples = new List<MeasurementSample>();
        using var listener = CreateHistogramListener("honua.spec.node_duration_ms", samples);

        var document = Document(ComputeNode("a"));
        _ = await CollectAsync(await fixture.Engine.StartAsync(document, new SpecApplyOptions(), CancellationToken.None));

        var failed = samples.Where(s => HasTag(s.Tags, "outcome", "failed")).ToList();
        Assert.Single(failed);
        Assert.True(failed[0].Value >= 0, "Failed node duration must be recorded.");
    }

    [Fact]
    public async Task Apply_ExecutorFailure_EmitsAppliesCompletedWithSuccessOutcome()
    {
        // The run itself completed cleanly — per-node failure does not turn
        // the apply-level counter into an `error` outcome. We still need the
        // ApplyDurationMs histogram to observe the run so dashboards don't
        // silently miss "one apply, zero duration samples" on the
        // fast-failure path.
        var fixture = new OrchestratorFixture();
        fixture.Executor.FailFor("a", new InvalidOperationException("boom"));

        var counterSamples = new List<MeasurementSample>();
        var histogramSamples = new List<MeasurementSample>();
        using var counterListener = CreateCounterListener("honua.spec.applies_completed", counterSamples);
        using var histogramListener = CreateHistogramListener("honua.spec.apply_duration_ms", histogramSamples);

        var document = Document(ComputeNode("a"));
        _ = await CollectAsync(await fixture.Engine.StartAsync(document, new SpecApplyOptions(), CancellationToken.None));

        Assert.Single(counterSamples);
        Assert.True(HasTag(counterSamples[0].Tags, "outcome", "ApplyCompleted"));
        Assert.Single(histogramSamples);
        Assert.True(HasTag(histogramSamples[0].Tags, "outcome", "ApplyCompleted"));
    }

    [Fact]
    public async Task Apply_Cancellation_RecordsApplyDurationWithCancelledOutcome()
    {
        // Terminal cancellation must emit the apply-level histogram so the
        // cancelled tail stays observable alongside the succeeded/error tails.
        var fixture = new OrchestratorFixture();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        fixture.Executor.HookBeforeExecute("slow", async ct =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
        });

        var samples = new List<MeasurementSample>();
        using var listener = CreateHistogramListener("honua.spec.apply_duration_ms", samples);

        var document = Document(ComputeNode("slow"));
        var handle = await fixture.Engine.StartAsync(document, new SpecApplyOptions(), CancellationToken.None);
        var collectTask = Task.Run(() => CollectAsync(handle));
        await started.Task;
        Assert.True(fixture.Engine.TryCancel(handle.ApplyToken));
        release.TrySetResult();
        _ = await collectTask;

        Assert.Single(samples);
        Assert.True(HasTag(samples[0].Tags, "outcome", "ApplyCancelled"));
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

    private static MeterListener CreateHistogramListener(string instrumentName, List<MeasurementSample> samples)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter == SpecTelemetry.Meter && instrument.Name == instrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, tags, _) =>
        {
            lock (samples)
            {
                samples.Add(new MeasurementSample(measurement, tags.ToArray()));
            }
        });
        listener.Start();
        return listener;
    }

    private static MeterListener CreateCounterListener(string instrumentName, List<MeasurementSample> samples)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter == SpecTelemetry.Meter && instrument.Name == instrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            lock (samples)
            {
                samples.Add(new MeasurementSample((double)measurement, tags.ToArray()));
            }
        });
        listener.Start();
        return listener;
    }

    private static bool HasTag(KeyValuePair<string, object?>[] tags, string name, string value)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == name && tag.Value is string s && s == value)
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct MeasurementSample(double Value, KeyValuePair<string, object?>[] Tags);

    private sealed class OrchestratorFixture
    {
        public OrchestratorFixture()
        {
            var catalog = new StubProcessCatalog();
            var estimator = new SpecCostEstimator(catalog);
            var planner = new SpecPlanner(estimator);
            Cache = new TrackingArtifactCache(new InMemoryContentHashArtifactCache());
            Executor = new FakeComputeExecutor();
            Tokens = new SpecApplyTokenRegistry();
            Engine = new SpecApplyOrchestrator(
                planner,
                Executor,
                Cache,
                Tokens);
        }

        public TrackingArtifactCache Cache { get; }

        public FakeComputeExecutor Executor { get; }

        public SpecApplyTokenRegistry Tokens { get; }

        public SpecApplyOrchestrator Engine { get; }
    }

    private sealed class TrackingArtifactCache : IContentHashArtifactCache
    {
        private readonly IContentHashArtifactCache _inner;
        private readonly ConcurrentBag<SpecArtifactPayload> _payloads = new();

        public TrackingArtifactCache(IContentHashArtifactCache inner)
        {
            _inner = inner;
        }

        public IEnumerable<SpecArtifactPayload> PutPayloads => _payloads;

        public Task<CachedArtifactRef?> TryGetAsync(string contentHash, CancellationToken cancellationToken = default)
            => _inner.TryGetAsync(contentHash, cancellationToken);

        public Task<Stream?> OpenReadAsync(string contentHash, CancellationToken cancellationToken = default)
            => _inner.OpenReadAsync(contentHash, cancellationToken);

        public Task<CachedArtifactRef> PutAsync(SpecArtifactPayload payload, CancellationToken cancellationToken = default)
        {
            _payloads.Add(payload);
            return _inner.PutAsync(payload, cancellationToken);
        }
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
