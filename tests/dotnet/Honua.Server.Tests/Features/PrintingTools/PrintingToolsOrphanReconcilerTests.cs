// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.PrintingTools;
using Honua.Server.Features.PrintingTools.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.PrintingTools;

/// <summary>
/// Restart leaves print jobs in the shared progress store with no in-process
/// channel entry. A new host must fail those jobs once their heartbeat lease expires.
/// </summary>
[Trait("Component", "PrintingTools")]
public class PrintingToolsOrphanReconcilerTests
{
    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public async Task RestartedHost_FailsQueuedPrintOnLaterHostedSweep()
    {
        var store = new MemoryProgressStore();
        var submitter = Submitter();
        await using var submitted = BuildProvider(store, new PrintWorkerInstance(), new PrintJobTracker());
        await SubmitAsync(submitted, submitter);

        var queued = store.Snapshot().OfType<PrintProgress>().Single();
        ShouldBeQueued(queued);

        var orphanOptions = new PrintingToolsOrphanOptions
        {
            ReconciliationInterval = TimeSpan.FromMilliseconds(20),
            LeaseTimeout = TimeSpan.FromMinutes(1)
        };
        await using var restarted = BuildProvider(store, new PrintWorkerInstance(), new PrintJobTracker(), orphanOptions);
        var reconciler = ActivatorUtilities.CreateInstance<PrintingToolsOrphanReconciler>(restarted);
        await reconciler.StartAsync(CancellationToken.None);
        try
        {
            // Safety cancellation bounds a hung hosted service; elapsed time is not the
            // functional oracle. The first immutable snapshot contains a fresh lease.
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await store.FirstEnumeration.WaitAsync(safety.Token);
            await store.SetProgressAsync(queued.JobId, queued with
            {
                HeartbeatAt = DateTimeOffset.UtcNow - orphanOptions.LeaseTimeout - TimeSpan.FromMinutes(1)
            }, cancellationToken: safety.Token);

            // The initial sweep cannot fail its fresh snapshot. A subsequent real periodic
            // hosted sweep must observe the replacement and perform the terminal CAS.
            var terminalEnumeration = await store.TerminalReplacement.WaitAsync(safety.Token);
            Assert.True(terminalEnumeration >= 2, "Orphan failure must occur during a later hosted sweep.");
            var body = await ReadStatusAsync(restarted, submitter, queued.JobId);
            Assert.Contains("esriJobFailed", body, StringComparison.Ordinal);
            Assert.Contains(PrintingToolsOrphanReconciler.RestartedMessage, body, StringComparison.Ordinal);
        }
        finally
        {
            await reconciler.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public async Task FreshHeartbeatFromOtherInstance_StaysSubmitted()
    {
        var store = new MemoryProgressStore();
        var submitter = Submitter();
        var orphanOptions = new PrintingToolsOrphanOptions
        {
            ReconciliationInterval = TimeSpan.FromMilliseconds(20),
            LeaseTimeout = TimeSpan.FromMinutes(5)
        };
        await using var restarted = BuildProvider(store, new PrintWorkerInstance(), new PrintJobTracker(), orphanOptions);
        await SubmitAsync(restarted, submitter);

        // Status reads authorize the canonical actor, not the raw name identifier. Keep the
        // submitter stamp from the real submit path, and only move the lease to another worker.
        var queued = store.Snapshot().OfType<PrintProgress>().Single();
        var foreign = queued with
        {
            OwnerInstanceId = "other-instance",
            HeartbeatAt = DateTimeOffset.UtcNow
        };
        await store.SetProgressAsync(foreign.JobId, foreign, TimeSpan.FromHours(24));

        var reconciler = ActivatorUtilities.CreateInstance<PrintingToolsOrphanReconciler>(restarted);
        await reconciler.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120));
            var body = await ReadStatusAsync(restarted, submitter, foreign.JobId);
            Assert.Contains("esriJobSubmitted", body, StringComparison.Ordinal);
            Assert.DoesNotContain("esriJobFailed", body, StringComparison.Ordinal);
        }
        finally
        {
            await reconciler.StopAsync(CancellationToken.None);
        }
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public async Task HeartbeatRefreshedAfterEnumeration_DoesNotFailLiveOwner()
    {
        var store = new MemoryProgressStore();
        await using var restarted = BuildProvider(store, new PrintWorkerInstance(), new PrintJobTracker(),
            new PrintingToolsOrphanOptions { LeaseTimeout = TimeSpan.FromSeconds(30) });
        await SubmitAsync(restarted, Submitter());
        var queued = store.Snapshot().OfType<PrintProgress>().Single();
        var stale = queued with { OwnerInstanceId = "live-owner", HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        await store.SetProgressAsync(stale.JobId, stale);
        var fresh = stale with { HeartbeatAt = DateTimeOffset.UtcNow };
        store.BeforeSnapshotReplace = () =>
        {
            store.BeforeSnapshotReplace = null;
            // This in-memory test store completes writes synchronously under its gate.
            _ = store.SetProgressAsync(stale.JobId, fresh);
        };
        var reconciler = ActivatorUtilities.CreateInstance<PrintingToolsOrphanReconciler>(restarted);
        var sweep = typeof(PrintingToolsOrphanReconciler).GetMethod("FailStaleJobsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)sweep.Invoke(reconciler, [CancellationToken.None])!;
        Assert.Equal(fresh, await store.GetProgressAsync<PrintProgress>(stale.JobId));
    }

    private static ServiceProvider BuildProvider(
        MemoryProgressStore store,
        PrintWorkerInstance instance,
        PrintJobTracker tracker,
        PrintingToolsOrphanOptions? options = null)
        => new ServiceCollection()
            .AddSingleton<IUniversalProgressStore>(store)
            .AddSingleton(Channel.CreateUnbounded<PrintJob>())
            .AddSingleton(instance)
            .AddSingleton(tracker)
            .AddSingleton<IOptions<PrintingToolsOrphanOptions>>(Options.Create(options ?? new PrintingToolsOrphanOptions()))
            .AddLogging()
            .BuildServiceProvider();

    private static ClaimsPrincipal Submitter()
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim("tenant_id", "tenant-a")],
            "test"));

    private static async Task SubmitAsync(ServiceProvider services, ClaimsPrincipal submitter)
    {
        var context = new DefaultHttpContext { RequestServices = services, User = submitter };
        var submit = typeof(PrintingToolsEndpoints).GetMethod(
            "SubmitJobInternalAsync",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        await (Task<IResult>)submit.Invoke(
            null,
            [context, new WebMapDefinition(), "PNG32", "MAP_ONLY", 96, NullLogger.Instance, CancellationToken.None])!;
    }

    private static async Task<string> ReadStatusAsync(ServiceProvider services, ClaimsPrincipal submitter, string jobId)
    {
        var context = new DefaultHttpContext { RequestServices = services, User = submitter };
        context.Request.RouteValues["jobId"] = jobId;
        context.Response.Body = new MemoryStream();
        var status = typeof(PrintingToolsEndpoints).GetMethod(
            "HandleJobStatus",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = await (Task<IResult>)status.Invoke(null, [context, CancellationToken.None])!;
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static void ShouldBeQueued(PrintProgress progress)
    {
        Assert.Equal(OperationStatus.Queued, progress.Status);
        Assert.False(string.IsNullOrWhiteSpace(progress.OwnerInstanceId));
        Assert.NotNull(progress.HeartbeatAt);
    }

    private sealed class MemoryProgressStore : IUniversalProgressStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, IOperationProgress> _items = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _firstEnumeration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _terminalReplacement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _enumerationCount;

        public Task FirstEnumeration => _firstEnumeration.Task;

        public Task<int> TerminalReplacement => _terminalReplacement.Task;

        public IOperationProgress[] Snapshot()
        {
            lock (_gate)
            {
                return _items.Values.ToArray();
            }
        }

        public Task SetProgressAsync(
            string operationId,
            IOperationProgress progress,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _items[operationId] = progress;
            }

            return Task.CompletedTask;
        }

        public Task<ProgressCompareAndSetResult> TrySetProgressAsync(
            string operationId,
            IOperationProgress progress,
            OperationStatus expectedStatus,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_items.TryGetValue(operationId, out var current))
                {
                    return Task.FromResult(ProgressCompareAndSetResult.NotFound);
                }

                if (current.Status != expectedStatus)
                {
                    return Task.FromResult(ProgressCompareAndSetResult.StatusMismatch(current));
                }

                _items[operationId] = progress;
                return Task.FromResult(ProgressCompareAndSetResult.Updated);
            }
        }

        public Action? BeforeSnapshotReplace { get; set; }

        public Task<ProgressCompareAndSetResult> TryReplaceProgressAsync(
            string operationId,
            IOperationProgress progress,
            IOperationProgress expectedProgress,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            BeforeSnapshotReplace?.Invoke();
            lock (_gate)
            {
                if (!_items.TryGetValue(operationId, out var current))
                {
                    return Task.FromResult(ProgressCompareAndSetResult.NotFound);
                }
                if (!Equals(current, expectedProgress))
                {
                    return Task.FromResult(ProgressCompareAndSetResult.StatusMismatch(current));
                }
                _items[operationId] = progress;
                if (progress.Status == OperationStatus.Failed)
                {
                    _terminalReplacement.TrySetResult(_enumerationCount);
                }
                return Task.FromResult(ProgressCompareAndSetResult.Updated);
            }
        }

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
        {
            lock (_gate)
            {
                return Task.FromResult(_items.TryGetValue(operationId, out var progress) ? progress as TProgress : null);
            }
        }

        public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_items.TryGetValue(operationId, out var progress) ? progress : null);
            }
        }

        public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _items.Remove(operationId);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(
            OperationType? operationType = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                IReadOnlyList<string> ids = _items
                    .Where(pair => operationType is null || pair.Value.Type == operationType)
                    .Select(pair => pair.Key)
                    .ToArray();
                return Task.FromResult(ids);
            }
        }

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(
            OperationType operationType,
            CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
        {
            lock (_gate)
            {
                IReadOnlyList<TProgress> matches = _items.Values
                    .OfType<TProgress>()
                    .Where(progress => progress.Type == operationType)
                    .ToArray();
                _enumerationCount++;
                // Capture the immutable snapshot before waking the test's mutation.
                _firstEnumeration.TrySetResult();
                return Task.FromResult(matches);
            }
        }
    }
}
