// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.TestKit.Helpers;

/// <summary>
/// Functional in-memory <see cref="IExecutionJobStore"/> for integration tests. Production only
/// registers a durable execution-job store when Redis (<c>IConnectionMultiplexer</c>) is present
/// (see GeoprocessingServiceCollectionExtensions), and the <see cref="WebAppFixture"/> runs without
/// Redis — so the durable-operation endpoint tests register this store to exercise the durable
/// cancel/retry paths (the endpoints resolve <see cref="IExecutionJobStore"/> optionally and take
/// the durable path when it is present). State is a plain dictionary; not thread-optimised, but the
/// per-test server is single-caller.
/// </summary>
public sealed class InMemoryExecutionJobStore : IExecutionJobStore
{
    private readonly Dictionary<string, ExecutionJobRecord> _jobs = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> TryCreateAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_jobs.ContainsKey(job.OperationId))
            {
                return Task.FromResult(false);
            }

            _jobs[job.OperationId] = job;
            return Task.FromResult(true);
        }
    }

    public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_jobs.TryGetValue(operationId, out var job) ? job : null);
        }
    }

    public Task SetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _jobs[job.OperationId] = job with { Version = job.Version + 1 };
            return Task.CompletedTask;
        }
    }

    public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _jobs[job.OperationId] = job with { Version = job.Version + 1 };
            return Task.FromResult(true);
        }
    }

    public Task<ExecutionJobPage> QueryAsync(ExecutionJobQuery query, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var items = _jobs.Values
                .OrderByDescending(job => job.CreatedAt)
                .Take(Math.Max(1, query.Limit))
                .ToArray();
            return Task.FromResult(new ExecutionJobPage { Items = items, NextCursor = null });
        }
    }

    public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<ExecutionJobRecord> items = _jobs.Values
                .Where(job => !kind.HasValue || job.Spec.Kind == kind.Value)
                .ToArray();
            return Task.FromResult(items);
        }
    }
}

/// <summary>
/// Functional in-memory <see cref="IJobQueue"/> for integration tests: a FIFO of operation ids with
/// real enqueue/remove/requeue/depth semantics so durable-operation endpoint tests can assert queue
/// removal on cancel. Pairs with <see cref="InMemoryExecutionJobStore"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Test double implementing IJobQueue; the 'Queue' suffix mirrors the abstraction it stands in for, matching the production RedisJobQueue naming.")]
public sealed class InMemoryJobQueue : IJobQueue
{
    private readonly List<string> _queue = new();
    private readonly object _gate = new();

    public Task EnqueueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_queue.Contains(operationId, StringComparer.Ordinal))
            {
                _queue.Add(operationId);
            }
        }

        return Task.CompletedTask;
    }

    public Task<string?> TryClaimAsync(string workerId, IReadOnlySet<ExecutionJobKind>? acceptedKinds = null, IReadOnlySet<string>? acceptedRuntimeProfiles = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                return Task.FromResult<string?>(null);
            }

            var operationId = _queue[0];
            _queue.RemoveAt(0);
            return Task.FromResult<string?>(operationId);
        }
    }

    public Task RequeueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, TimeSpan? visibleAfter = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_queue.Contains(operationId, StringComparer.Ordinal))
            {
                _queue.Add(operationId);
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string operationId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _queue.RemoveAll(id => string.Equals(id, operationId, StringComparison.Ordinal));
        }

        return Task.CompletedTask;
    }

    public Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult((long)_queue.Count);
        }
    }
}
