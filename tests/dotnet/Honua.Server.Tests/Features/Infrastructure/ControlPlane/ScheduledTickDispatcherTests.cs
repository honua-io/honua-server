// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Phase 3 dispatcher-seam tests: the scheduled-tick dispatcher routes each tick kind to the handler
/// that owns it and runs that handler's tick exactly once, and does nothing else (no extra
/// coordination). Mirrors <see cref="OperationReconcileDispatcherTests"/> for the PERIODIC services.
/// </summary>
public sealed class ScheduledTickDispatcherTests
{
    private sealed class RecordingHandler(ScheduledTickKind kind) : IScheduledTickHandler
    {
        public ScheduledTickKind Kind { get; } = kind;

        public int RunCount { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public Task RunTickAsync(CancellationToken cancellationToken = default)
        {
            RunCount++;
            LastToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private static (ScheduledTickDispatcher Dispatcher, IReadOnlyDictionary<ScheduledTickKind, RecordingHandler> Handlers)
        BuildDispatcherForAllKinds()
    {
        var handlers = Enum.GetValues<ScheduledTickKind>()
            .ToDictionary(kind => kind, kind => new RecordingHandler(kind));
        var dispatcher = new ScheduledTickDispatcher(handlers.Values);
        return (dispatcher, handlers);
    }

    [Theory]
    [InlineData(ScheduledTickKind.WorkflowSchedule)]
    [InlineData(ScheduledTickKind.JobReconciliation)]
    [InlineData(ScheduledTickKind.TileCacheExpiry)]
    [InlineData(ScheduledTickKind.TileCacheEviction)]
    [InlineData(ScheduledTickKind.WorkspaceCleanup)]
    [InlineData(ScheduledTickKind.FileStorageCleanup)]
    [InlineData(ScheduledTickKind.RasterOutputReconciliation)]
    [InlineData(ScheduledTickKind.TemporaryFileCleanup)]
    [InlineData(ScheduledTickKind.DigestFlush)]
    public async Task RunTickAsync_RoutesToTheMatchingHandlerOnly(ScheduledTickKind kind)
    {
        var (dispatcher, handlers) = BuildDispatcherForAllKinds();

        await dispatcher.RunTickAsync(kind);

        handlers[kind].RunCount.Should().Be(1, "the dispatcher routes the tick to the handler that owns its kind");
        foreach (var (otherKind, handler) in handlers)
        {
            if (otherKind == kind)
            {
                continue;
            }

            handler.RunCount.Should().Be(0, "no other handler should be invoked for kind {0}", kind);
        }
    }

    [Fact]
    public async Task RunTickAsync_RunsTheTickExactlyOncePerInvocation()
    {
        var (dispatcher, handlers) = BuildDispatcherForAllKinds();

        await dispatcher.RunTickAsync(ScheduledTickKind.DigestFlush);
        await dispatcher.RunTickAsync(ScheduledTickKind.DigestFlush);

        handlers[ScheduledTickKind.DigestFlush].RunCount
            .Should().Be(2, "each invocation runs the tick once — the dispatcher adds no batching or dedupe of its own");
    }

    [Fact]
    public async Task RunTickAsync_ForwardsTheCancellationToken()
    {
        var (dispatcher, handlers) = BuildDispatcherForAllKinds();
        using var cts = new CancellationTokenSource();

        await dispatcher.RunTickAsync(ScheduledTickKind.WorkflowSchedule, cts.Token);

        handlers[ScheduledTickKind.WorkflowSchedule].LastToken
            .Should().Be(cts.Token, "the dispatcher forwards the caller's token to the tick body");
    }

    [Fact]
    public async Task RunTickAsync_UnregisteredKind_Throws()
    {
        // Only the WorkflowSchedule handler is registered (e.g. every other feature is disabled in
        // this deployment). Driving an unregistered kind must surface a clear error so the scheduler
        // can retry/log rather than silently no-op.
        var dispatcher = new ScheduledTickDispatcher(
            [new RecordingHandler(ScheduledTickKind.WorkflowSchedule)]);

        var act = async () => await dispatcher.RunTickAsync(ScheduledTickKind.DigestFlush);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_DuplicateKind_DoesNotThrow()
    {
        // A duplicate registration for one kind is a registration mistake, but it must not crash
        // composition of an otherwise healthy host; last-registered wins.
        var act = () => new ScheduledTickDispatcher(
        [
            new RecordingHandler(ScheduledTickKind.DigestFlush),
            new RecordingHandler(ScheduledTickKind.DigestFlush)
        ]);

        act.Should().NotThrow();
    }
}
