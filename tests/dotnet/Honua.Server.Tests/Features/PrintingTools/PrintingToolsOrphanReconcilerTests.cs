// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    public async Task RestartedHost_FailsQueuedPrintWithinReconciliationInterval()
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
            LeaseTimeout = TimeSpan.FromMilliseconds(40)
        };
        await using var restarted = BuildProvider(store, new PrintWorkerInstance(), new PrintJobTracker(), orphanOptions);
        var reconciler = ActivatorUtilities.CreateInstance<PrintingToolsOrphanReconciler>(restarted);
        await reconciler.StartAsync(CancellationToken.None);
        try
        {
            var body = await PollStatusAsync(restarted, submitter, queued.JobId, TimeSpan.FromSeconds(2));
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
        var now = DateTimeOffset.UtcNow;
        var progress = PrintProgress.CreateInitial("print-live", "PNG32", "MAP_ONLY", 1) with
        {
            RequestedBy = "user-1",
            TenantId = "tenant-a",
            AnonymousSubmission = false,
            OwnerInstanceId = "other-instance",
            HeartbeatAt = now
        };
        await store.SetProgressAsync(progress.JobId, progress, TimeSpan.FromHours(24));

        var orphanOptions = new PrintingToolsOrphanOptions
        {
            ReconciliationInterval = TimeSpan.FromMilliseconds(20),
            LeaseTimeout = TimeSpan.FromMinutes(5)
        };
        await using var restarted = BuildProvider(store, new PrintWorkerInstance(), new PrintJobTracker(), orphanOptions);
        var reconciler = ActivatorUtilities.CreateInstance<PrintingToolsOrphanReconciler>(restarted);
        await reconciler.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120));
            var body = await ReadStatusAsync(restarted, Submitter(), progress.JobId);
            Assert.Contains("esriJobSubmitted", body, StringComparison.Ordinal);
            Assert.DoesNotContain("esriJobFailed", body, StringComparison.Ordinal);
        }
        finally
        {
            await reconciler.StopAsync(CancellationToken.None);
        }
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

    private static async Task<string> PollStatusAsync(
        ServiceProvider services,
        ClaimsPrincipal submitter,
        string jobId,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string body = "";
        while (DateTime.UtcNow < deadline)
        {
            body = await ReadStatusAsync(services, submitter, jobId);
            if (body.Contains("esriJobFailed", StringComparison.Ordinal))
            {
                return body;
            }

            await Task.Delay(15);
        }

        return body;
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
                return Task.FromResult(matches);
            }
        }
    }
}
