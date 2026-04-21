// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Spec.Services;

/// <summary>
/// Default <see cref="ISpecApplyEngine"/> implementation. Plans the spec, then
/// drives the DAG through a bounded drop-oldest <see cref="Channel{T}"/> of
/// <see cref="SpecApplyEvent"/>s. Events are emitted in the order they occur
/// and are consumed exactly once by a single reader (SSE or gRPC). The buffer
/// caps memory if a client detaches mid-apply — the run outlives the request,
/// so unbounded writes would retain the whole backlog until completion.
/// </summary>
internal sealed partial class SpecApplyOrchestrator : ISpecApplyEngine
{
    // Bounded so a disconnected reader cannot inflate unbounded memory; deep
    // enough that a connected reader keeps up without dropping. Sequence
    // numbers are already documented as drop-detectable by clients, so the
    // DropOldest policy satisfies the contract.
    private const int EventBufferCapacity = 256;

    private readonly ISpecPlanner _planner;
    private readonly ISpecComputeExecutor _executor;
    private readonly IContentHashArtifactCache _cache;
    private readonly SpecApplyTokenRegistry _tokens;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SpecApplyOrchestrator> _logger;

    public SpecApplyOrchestrator(
        ISpecPlanner planner,
        ISpecComputeExecutor executor,
        IContentHashArtifactCache cache,
        SpecApplyTokenRegistry tokens,
        TimeProvider? timeProvider = null,
        ILogger<SpecApplyOrchestrator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(tokens);
        _planner = planner;
        _executor = executor;
        _cache = cache;
        _tokens = tokens;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SpecApplyOrchestrator>.Instance;
    }

    public async Task<SpecApplyHandle> StartAsync(
        CanonicalSpecDocument document,
        SpecApplyOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var plan = await _planner.PlanAsync(document, cancellationToken).ConfigureAwait(false);

        // Gate the run: fatal plan diagnostics (duplicate ids, cycles,
        // unresolved references) must be surfaced as a document-level failure
        // so the HTTP/gRPC adapters can translate to 400/InvalidArgument.
        // Without this gate, duplicate ids throw ArgumentException from the
        // ToDictionary below, and cycles collapse into a zero-node apply that
        // ends with ApplyCompleted instead of rejecting the document.
        var fatalDiagnostics = CollectFatalDiagnostics(plan);
        if (fatalDiagnostics.Count > 0)
        {
            throw new SpecDocumentInvalidException(fatalDiagnostics);
        }

        var applyToken = Guid.NewGuid().ToString("n");
        // Server-owned CTS: the apply run must outlive the originating
        // request. Client disconnects on SSE or gRPC cancel the outbound
        // stream only; the background run keeps going until TryCancel (via
        // /v1/spec/cancel or gRPC CancelApply) trips this token.
        var cts = new CancellationTokenSource();
        _tokens.Register(applyToken, cts);

        var channel = Channel.CreateBounded<SpecApplyEvent>(new BoundedChannelOptions(EventBufferCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        SpecTelemetry.AppliesStarted.Add(1,
            new KeyValuePair<string, object?>("cache_mode", options.CacheMode.ToString()));

        var nodesById = document.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var runContext = new ApplyRunContext(
            applyToken,
            plan,
            nodesById,
            options,
            channel.Writer,
            _executor,
            _cache,
            _timeProvider,
            _logger);

        _ = Task.Run(() => RunAsync(runContext, cts), CancellationToken.None);

        return new SpecApplyHandle(applyToken, ReadEventsAsync(channel.Reader, applyToken), plan);
    }

    private static IReadOnlyList<SpecWarning> CollectFatalDiagnostics(SpecPlan plan)
    {
        List<SpecWarning>? fatal = null;
        foreach (var warning in plan.Warnings)
        {
            if (warning.Severity != SpecDiagnosticSeverity.Error)
            {
                continue;
            }

            fatal ??= new List<SpecWarning>();
            fatal.Add(warning);
        }

        return (IReadOnlyList<SpecWarning>?)fatal ?? Array.Empty<SpecWarning>();
    }

    public bool TryCancel(string applyToken) => _tokens.TryCancel(applyToken);

    private async Task RunAsync(ApplyRunContext ctx, CancellationTokenSource cts)
    {
        using var activity = SpecTelemetry.ActivitySource.StartActivity(SpecTelemetry.ApplyActivityName);
        activity?.SetTag("honua.spec.apply_token", ctx.ApplyToken);
        activity?.SetTag("honua.spec.plan_id", ctx.Plan.PlanId);
        activity?.SetTag("honua.spec.total_nodes", ctx.Plan.Nodes.Count);

        var stopwatch = Stopwatch.StartNew();
        long sequence = 0;

        await ctx.Writer.WriteAsync(new SpecApplyEvent
        {
            Sequence = Interlocked.Increment(ref sequence),
            Kind = SpecApplyEventKind.ApplyStarted,
            ApplyToken = ctx.ApplyToken,
            Timestamp = ctx.TimeProvider.GetUtcNow()
        }, cts.Token).ConfigureAwait(false);

        var nodeState = new ConcurrentDictionary<string, NodeRunState>(StringComparer.Ordinal);
        foreach (var n in ctx.Plan.Nodes)
        {
            nodeState[n.NodeId] = NodeRunState.Pending;
        }

        var counters = new ApplyCounters();
        var cachedInputs = new ConcurrentDictionary<string, CachedArtifactRef>(StringComparer.Ordinal);

        try
        {
            using var gate = new SemaphoreSlim(Math.Max(1, ctx.Options.MaxConcurrency));
            await ProcessLevelsAsync(ctx, cts, sequenceRef: () => Interlocked.Increment(ref sequence),
                nodeState, counters, cachedInputs, gate).ConfigureAwait(false);

            var terminalKind = cts.Token.IsCancellationRequested
                ? SpecApplyEventKind.ApplyCancelled
                : SpecApplyEventKind.ApplyCompleted;

            var summary = new SpecApplySummary
            {
                TotalNodes = ctx.Plan.Nodes.Count,
                CachedNodes = counters.Cached,
                RanNodes = counters.Ran,
                FailedNodes = counters.Failed,
                SkippedNodes = counters.Skipped,
                TotalDurationMs = stopwatch.Elapsed.TotalMilliseconds,
                Cancelled = cts.Token.IsCancellationRequested
            };

            await ctx.Writer.WriteAsync(new SpecApplyEvent
            {
                Sequence = Interlocked.Increment(ref sequence),
                Kind = terminalKind,
                ApplyToken = ctx.ApplyToken,
                Timestamp = ctx.TimeProvider.GetUtcNow(),
                Summary = summary
            }, CancellationToken.None).ConfigureAwait(false);

            SpecTelemetry.AppliesCompleted.Add(1,
                new KeyValuePair<string, object?>("outcome", terminalKind.ToString()));
            SpecTelemetry.ApplyDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("outcome", terminalKind.ToString()));
            SpecTelemetry.SetSuccess(activity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SpecTelemetry.RecordException(activity, ex);
            LogApplyTerminatedWithException(_logger, ctx.ApplyToken, ex);
            try
            {
                await ctx.Writer.WriteAsync(new SpecApplyEvent
                {
                    Sequence = Interlocked.Increment(ref sequence),
                    Kind = SpecApplyEventKind.ApplyCompleted,
                    ApplyToken = ctx.ApplyToken,
                    Timestamp = ctx.TimeProvider.GetUtcNow(),
                    Diagnostic = new SpecWarning
                    {
                        Code = "spec-apply-unhandled",
                        Message = "Apply terminated unexpectedly.",
                        Severity = SpecDiagnosticSeverity.Error
                    },
                    Summary = new SpecApplySummary
                    {
                        TotalNodes = ctx.Plan.Nodes.Count,
                        CachedNodes = counters.Cached,
                        RanNodes = counters.Ran,
                        FailedNodes = counters.Failed + 1,
                        SkippedNodes = counters.Skipped,
                        TotalDurationMs = stopwatch.Elapsed.TotalMilliseconds,
                        Cancelled = false
                    }
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore — writer may already be completed.
            }
        }
        finally
        {
            ctx.Writer.TryComplete();
            _tokens.Release(ctx.ApplyToken);
            cts.Dispose();
        }
    }

    private async Task ProcessLevelsAsync(
        ApplyRunContext ctx,
        CancellationTokenSource cts,
        Func<long> sequenceRef,
        ConcurrentDictionary<string, NodeRunState> nodeState,
        ApplyCounters counters,
        ConcurrentDictionary<string, CachedArtifactRef> cachedInputs,
        SemaphoreSlim gate)
    {
        var remaining = new HashSet<string>(ctx.Plan.Nodes.Select(n => n.NodeId), StringComparer.Ordinal);
        var nodesById = ctx.Plan.Nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(id => IsReady(id, nodesById, nodeState))
                .ToList();

            if (ready.Count == 0)
            {
                // No node is ready — either everything waiting on failed
                // upstream, or cycle (shouldn't happen). Under cancellation
                // the reason is the cancel itself; otherwise it's an upstream
                // failure that stalled the frontier.
                var reason = cts.Token.IsCancellationRequested
                    ? SkipReason.Cancelled
                    : SkipReason.UpstreamFailed;
                foreach (var id in remaining.ToList())
                {
                    nodeState[id] = NodeRunState.Skipped;
                    Interlocked.Increment(ref counters.SkippedRef);
                    await EmitSkippedAsync(ctx, sequenceRef, id, reason).ConfigureAwait(false);
                }

                break;
            }

            var batch = new List<Task>(ready.Count);
            foreach (var nodeId in ready)
            {
                remaining.Remove(nodeId);
                var plannedNode = nodesById[nodeId];
                batch.Add(ProcessNodeAsync(ctx, cts, sequenceRef, plannedNode, nodeState, counters, cachedInputs, gate));
            }

            await Task.WhenAll(batch).ConfigureAwait(false);
        }
    }

    private static async Task EmitSkippedAsync(
        ApplyRunContext ctx,
        Func<long> sequenceRef,
        string nodeId,
        SkipReason reason)
    {
        var (code, message) = reason switch
        {
            SkipReason.Cancelled => (
                SpecDiagnosticCodes.ApplyCancelled,
                $"Node '{nodeId}' skipped — apply was cancelled before the node started."),
            _ => (
                SpecDiagnosticCodes.UpstreamFailed,
                $"Node '{nodeId}' skipped — an upstream dependency failed.")
        };

        await ctx.Writer.WriteAsync(new SpecApplyEvent
        {
            Sequence = sequenceRef(),
            Kind = SpecApplyEventKind.Skipped,
            ApplyToken = ctx.ApplyToken,
            NodeId = nodeId,
            Timestamp = ctx.TimeProvider.GetUtcNow(),
            Diagnostic = new SpecWarning
            {
                Code = code,
                Message = message,
                Severity = SpecDiagnosticSeverity.Warning,
                NodeId = nodeId
            }
        }, CancellationToken.None).ConfigureAwait(false);

        SpecTelemetry.NodesProcessed.Add(1,
            new KeyValuePair<string, object?>("outcome", "skipped"));
    }

    private static bool IsReady(
        string nodeId,
        Dictionary<string, SpecPlanNode> nodesById,
        IReadOnlyDictionary<string, NodeRunState> nodeState)
    {
        var plannedNode = nodesById[nodeId];
        foreach (var dep in plannedNode.DependsOn)
        {
            var state = nodeState[dep];
            if (state is NodeRunState.Pending or NodeRunState.Running)
            {
                return false;
            }

            if (state is NodeRunState.Failed or NodeRunState.Skipped)
            {
                // Upstream failed/skipped — this node must be short-circuited
                // as skipped in the same batch, so IsReady returns true so the
                // batch can handle it explicitly. We only gate on running/pending.
                return true;
            }
        }

        return true;
    }

    private async Task ProcessNodeAsync(
        ApplyRunContext ctx,
        CancellationTokenSource cts,
        Func<long> sequenceRef,
        SpecPlanNode plannedNode,
        ConcurrentDictionary<string, NodeRunState> nodeState,
        ApplyCounters counters,
        ConcurrentDictionary<string, CachedArtifactRef> cachedInputs,
        SemaphoreSlim gate)
    {
        // Short-circuit if any upstream failed or was skipped. Cancellation
        // wins over dependency-failure here — a cancelled run should emit
        // `apply-cancelled`, not `upstream-failed`, for every deferred node.
        if (cts.Token.IsCancellationRequested)
        {
            nodeState[plannedNode.NodeId] = NodeRunState.Skipped;
            Interlocked.Increment(ref counters.SkippedRef);
            await EmitSkippedAsync(ctx, sequenceRef, plannedNode.NodeId, SkipReason.Cancelled).ConfigureAwait(false);
            return;
        }

        foreach (var dep in plannedNode.DependsOn)
        {
            if (nodeState[dep] is NodeRunState.Failed or NodeRunState.Skipped)
            {
                nodeState[plannedNode.NodeId] = NodeRunState.Skipped;
                Interlocked.Increment(ref counters.SkippedRef);
                await EmitSkippedAsync(ctx, sequenceRef, plannedNode.NodeId, SkipReason.UpstreamFailed).ConfigureAwait(false);
                return;
            }
        }

        // Reserved kinds (Dataset/Service/App) always hard-fail in S1. The
        // placeholder ReservedSpecResourceStateStore is wired by AddSpecCore
        // purely to reserve the DI slot; using store presence as a support
        // signal would let the DI wiring mask the error and route these nodes
        // through the compute executor. Keep this gate kind-based until S2
        // ships real resource-state stores.
        if (plannedNode.Kind is SpecResourceKind.Dataset or SpecResourceKind.Service or SpecResourceKind.App)
        {
            nodeState[plannedNode.NodeId] = NodeRunState.Failed;
            Interlocked.Increment(ref counters.FailedRef);
            await EmitFailedAsync(ctx, sequenceRef, plannedNode.NodeId, new SpecWarning
            {
                Code = SpecDiagnosticCodes.SpecKindNotInS1,
                Message = $"Node kind '{plannedNode.Kind}' is reserved for a future release.",
                Severity = SpecDiagnosticSeverity.Error,
                NodeId = plannedNode.NodeId,
                Remedy = "Restructure the spec to a compute/report node for the current release."
            }).ConfigureAwait(false);
            return;
        }

        await gate.WaitAsync(cts.Token).ConfigureAwait(false);
        try
        {
            await ctx.Writer.WriteAsync(new SpecApplyEvent
            {
                Sequence = sequenceRef(),
                Kind = SpecApplyEventKind.Queued,
                ApplyToken = ctx.ApplyToken,
                NodeId = plannedNode.NodeId,
                Timestamp = ctx.TimeProvider.GetUtcNow()
            }, cts.Token).ConfigureAwait(false);

            using var nodeActivity = SpecTelemetry.ActivitySource.StartActivity(SpecTelemetry.NodeActivityName);
            nodeActivity?.SetTag("honua.spec.node_id", plannedNode.NodeId);
            nodeActivity?.SetTag("honua.spec.node_kind", plannedNode.Kind.ToString());
            nodeActivity?.SetTag("honua.spec.content_hash", plannedNode.ContentHash);

            if (ctx.Options.CacheMode is not SpecCacheMode.Bypass)
            {
                var hit = await ctx.Cache.TryGetAsync(plannedNode.ContentHash, cts.Token).ConfigureAwait(false);
                if (hit is not null)
                {
                    cachedInputs[plannedNode.NodeId] = hit;
                    nodeState[plannedNode.NodeId] = NodeRunState.Cached;
                    Interlocked.Increment(ref counters.CachedRef);

                    SpecTelemetry.CacheLookups.Add(1,
                        new KeyValuePair<string, object?>("outcome", "hit"));
                    SpecTelemetry.NodesProcessed.Add(1,
                        new KeyValuePair<string, object?>("outcome", "cached"));

                    await ctx.Writer.WriteAsync(new SpecApplyEvent
                    {
                        Sequence = sequenceRef(),
                        Kind = SpecApplyEventKind.Cached,
                        ApplyToken = ctx.ApplyToken,
                        NodeId = plannedNode.NodeId,
                        ContentHash = plannedNode.ContentHash,
                        Timestamp = ctx.TimeProvider.GetUtcNow(),
                        ActualCost = new SpecCostActual
                        {
                            Bytes = hit.Bytes,
                            DurationMs = 0
                        }
                    }, cts.Token).ConfigureAwait(false);
                    return;
                }

                SpecTelemetry.CacheLookups.Add(1,
                    new KeyValuePair<string, object?>("outcome", "miss"));

                // ReadOnly is "dry-run against a warm cache" — never invoke the
                // executor and never fabricate a retrievable hash. Emitting
                // Succeeded with a hash that is not in the cache breaks the
                // `GET /v1/spec/artifact/{hash}` contract because the artifact
                // endpoint only serves entries present in the cache.
                if (ctx.Options.CacheMode is SpecCacheMode.ReadOnly)
                {
                    nodeState[plannedNode.NodeId] = NodeRunState.Failed;
                    Interlocked.Increment(ref counters.FailedRef);
                    await EmitFailedAsync(ctx, sequenceRef, plannedNode.NodeId, new SpecWarning
                    {
                        Code = SpecDiagnosticCodes.ReadOnlyCacheMiss,
                        Message = $"Node '{plannedNode.NodeId}' missed the cache under ReadOnly mode.",
                        Severity = SpecDiagnosticSeverity.Error,
                        NodeId = plannedNode.NodeId,
                        Remedy = "Warm the cache with ReadWrite first, or switch to ReadWrite/Bypass for this apply."
                    }).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                SpecTelemetry.CacheLookups.Add(1,
                    new KeyValuePair<string, object?>("outcome", "bypass"));
            }

            nodeState[plannedNode.NodeId] = NodeRunState.Running;
            await ctx.Writer.WriteAsync(new SpecApplyEvent
            {
                Sequence = sequenceRef(),
                Kind = SpecApplyEventKind.Running,
                ApplyToken = ctx.ApplyToken,
                NodeId = plannedNode.NodeId,
                ContentHash = plannedNode.ContentHash,
                Timestamp = ctx.TimeProvider.GetUtcNow()
            }, cts.Token).ConfigureAwait(false);

            var node = ctx.NodesById[plannedNode.NodeId];
            var inputs = BuildInputs(plannedNode, cachedInputs);

            var nodeStopwatch = Stopwatch.StartNew();
            try
            {
                var result = await ctx.Executor.ExecuteAsync(node, plannedNode.ContentHash, inputs, cts.Token)
                    .ConfigureAwait(false);
                nodeStopwatch.Stop();

                // ReadOnly cache-miss is now short-circuited earlier, so the
                // only paths reaching PutAsync are ReadWrite and Bypass. Stamp
                // TTL for nodes the planner flagged as mutable-source-no-pin
                // so those entries degrade instead of living forever.
                var payload = StampTtlIfMutable(result.Payload, plannedNode, ctx.Options.MutableSourceTtl);
                var reference = await ctx.Cache.PutAsync(payload, cts.Token).ConfigureAwait(false);
                cachedInputs[plannedNode.NodeId] = reference;

                nodeState[plannedNode.NodeId] = NodeRunState.Succeeded;
                Interlocked.Increment(ref counters.RanRef);

                SpecTelemetry.NodesProcessed.Add(1,
                    new KeyValuePair<string, object?>("outcome", "succeeded"));
                SpecTelemetry.NodeDurationMs.Record(nodeStopwatch.Elapsed.TotalMilliseconds);

                foreach (var warning in result.Warnings)
                {
                    await ctx.Writer.WriteAsync(new SpecApplyEvent
                    {
                        Sequence = sequenceRef(),
                        Kind = SpecApplyEventKind.Warning,
                        ApplyToken = ctx.ApplyToken,
                        NodeId = plannedNode.NodeId,
                        ContentHash = plannedNode.ContentHash,
                        Timestamp = ctx.TimeProvider.GetUtcNow(),
                        Diagnostic = warning
                    }, cts.Token).ConfigureAwait(false);
                }

                await ctx.Writer.WriteAsync(new SpecApplyEvent
                {
                    Sequence = sequenceRef(),
                    Kind = SpecApplyEventKind.Succeeded,
                    ApplyToken = ctx.ApplyToken,
                    NodeId = plannedNode.NodeId,
                    ContentHash = plannedNode.ContentHash,
                    Timestamp = ctx.TimeProvider.GetUtcNow(),
                    ActualCost = result.ActualCost
                }, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
            {
                nodeStopwatch.Stop();
                nodeState[plannedNode.NodeId] = NodeRunState.Skipped;
                Interlocked.Increment(ref counters.SkippedRef);
                await ctx.Writer.WriteAsync(new SpecApplyEvent
                {
                    Sequence = sequenceRef(),
                    Kind = SpecApplyEventKind.Skipped,
                    ApplyToken = ctx.ApplyToken,
                    NodeId = plannedNode.NodeId,
                    Timestamp = ctx.TimeProvider.GetUtcNow(),
                    Diagnostic = new SpecWarning
                    {
                        Code = SpecDiagnosticCodes.ApplyCancelled,
                        Message = $"Node '{plannedNode.NodeId}' cancelled before completion.",
                        Severity = SpecDiagnosticSeverity.Warning,
                        NodeId = plannedNode.NodeId
                    }
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (SpecExecutionException specEx)
            {
                nodeStopwatch.Stop();
                nodeState[plannedNode.NodeId] = NodeRunState.Failed;
                Interlocked.Increment(ref counters.FailedRef);
                SpecTelemetry.RecordException(nodeActivity, specEx);
                await EmitFailedAsync(ctx, sequenceRef, plannedNode.NodeId, specEx.ToWarning()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                nodeStopwatch.Stop();
                nodeState[plannedNode.NodeId] = NodeRunState.Failed;
                Interlocked.Increment(ref counters.FailedRef);
                SpecTelemetry.RecordException(nodeActivity, ex);
                LogNodeFailed(_logger, plannedNode.NodeId, ctx.ApplyToken, ex);
                await EmitFailedAsync(ctx, sequenceRef, plannedNode.NodeId, new SpecWarning
                {
                    Code = "spec-node-failed",
                    Message = SpecTelemetry.SanitizeTelemetryText(ex.Message),
                    Severity = SpecDiagnosticSeverity.Error,
                    NodeId = plannedNode.NodeId
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static SpecArtifactPayload StampTtlIfMutable(
        SpecArtifactPayload payload,
        SpecPlanNode plannedNode,
        TimeSpan? ttl)
    {
        if (ttl is null || payload.Ttl is not null)
        {
            return payload;
        }

        // The planner emits `mutable-source-no-pin` on a per-node basis; any
        // per-node Warning with that code means the artifact is derived from a
        // mutable source without a pin and must degrade via TTL.
        foreach (var warning in plannedNode.Warnings)
        {
            if (string.Equals(warning.Code, SpecDiagnosticCodes.MutableSourceNoPin, StringComparison.Ordinal))
            {
                return payload with { Ttl = ttl };
            }
        }

        return payload;
    }

    private static Dictionary<string, CachedArtifactRef> BuildInputs(
        SpecPlanNode plannedNode,
        ConcurrentDictionary<string, CachedArtifactRef> cachedInputs)
    {
        if (plannedNode.DependsOn.Count == 0)
        {
            return new Dictionary<string, CachedArtifactRef>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, CachedArtifactRef>(plannedNode.DependsOn.Count, StringComparer.Ordinal);
        foreach (var dep in plannedNode.DependsOn)
        {
            if (cachedInputs.TryGetValue(dep, out var reference))
            {
                result[dep] = reference;
            }
        }

        return result;
    }

    private static async Task EmitFailedAsync(
        ApplyRunContext ctx,
        Func<long> sequenceRef,
        string nodeId,
        SpecWarning diagnostic)
    {
        await ctx.Writer.WriteAsync(new SpecApplyEvent
        {
            Sequence = sequenceRef(),
            Kind = SpecApplyEventKind.Failed,
            ApplyToken = ctx.ApplyToken,
            NodeId = nodeId,
            Timestamp = ctx.TimeProvider.GetUtcNow(),
            Diagnostic = diagnostic
        }, CancellationToken.None).ConfigureAwait(false);

        SpecTelemetry.NodesProcessed.Add(1,
            new KeyValuePair<string, object?>("outcome", "failed"));
    }

    private static async IAsyncEnumerable<SpecApplyEvent> ReadEventsAsync(
        ChannelReader<SpecApplyEvent> reader,
        string applyToken)
    {
        _ = applyToken; // reserved for future filtering; keeps call-site self-documenting
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var evt))
            {
                yield return evt;
            }
        }
    }

    private enum NodeRunState
    {
        Pending,
        Running,
        Succeeded,
        Failed,
        Skipped,
        Cached
    }

    private enum SkipReason
    {
        UpstreamFailed,
        Cancelled
    }

    private sealed class ApplyCounters
    {
        public int CachedRef;
        public int RanRef;
        public int FailedRef;
        public int SkippedRef;

        public int Cached => CachedRef;

        public int Ran => RanRef;

        public int Failed => FailedRef;

        public int Skipped => SkippedRef;
    }

    private sealed record ApplyRunContext(
        string ApplyToken,
        SpecPlan Plan,
        IReadOnlyDictionary<string, CanonicalSpecNode> NodesById,
        SpecApplyOptions Options,
        ChannelWriter<SpecApplyEvent> Writer,
        ISpecComputeExecutor Executor,
        IContentHashArtifactCache Cache,
        TimeProvider TimeProvider,
        ILogger Logger);

    [LoggerMessage(
        EventId = 11001,
        Level = LogLevel.Error,
        Message = "Apply run {ApplyToken} terminated with an unhandled exception.")]
    private static partial void LogApplyTerminatedWithException(
        ILogger logger, string applyToken, Exception exception);

    [LoggerMessage(
        EventId = 11002,
        Level = LogLevel.Error,
        Message = "Node {NodeId} failed during apply {ApplyToken}.")]
    private static partial void LogNodeFailed(
        ILogger logger, string nodeId, string applyToken, Exception exception);
}
