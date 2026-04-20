// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Redis-backed durable store for run-to-completion execution jobs and reconciliation leases.
/// </summary>
internal sealed partial class RedisExecutionJobStore(
    IConnectionMultiplexer redis,
    ILogger<RedisExecutionJobStore> logger) : IExecutionJobStore
{
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    private const string CasSetScript = """
        local current = redis.call('GET', KEYS[1])
        if current == false then return 0 end
        local v = tonumber(string.match(current, '"version":(%d+)')) or 0
        if v ~= tonumber(ARGV[1]) then return 0 end
        redis.call('SET', KEYS[1], ARGV[2], 'PX', tonumber(ARGV[3]))
        return 1
        """;

    private readonly IDatabase _database = redis.GetDatabase();

    public Task<bool> TryAcquireLeaseAsync(
        string operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _database.LockTakeAsync(GetLeaseKey(operationId), ownerId, leaseDuration);
    }

    public Task<bool> RenewLeaseAsync(
        string operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _database.LockExtendAsync(GetLeaseKey(operationId), ownerId, leaseDuration);
    }

    public Task ReleaseLeaseAsync(
        string operationId,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _database.LockReleaseAsync(GetLeaseKey(operationId), ownerId);
    }

    public async Task<bool> TryCreateAsync(
        ExecutionJobRecord job,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        var versioned = job with { Version = 1 };
        var payload = JsonSerializer.Serialize(versioned, ControlPlaneJsonContext.Default.ExecutionJobRecord);
        var created = await _database.StringSetAsync(
                GetJobKey(job.OperationId),
                payload,
                ttl ?? DefaultRetention,
                when: When.NotExists)
            .ConfigureAwait(false);

        if (!created)
        {
            return false;
        }

        await UpdateActiveIndexesAsync(versioned).ConfigureAwait(false);
        Log.ExecutionJobCreated(logger, job.OperationId, versioned.Spec.Kind.ToString(), versioned.Status.ToString());
        return true;
    }

    public async Task<ExecutionJobRecord?> GetAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var payload = await _database.StringGetAsync(GetJobKey(operationId)).ConfigureAwait(false);
        if (!payload.HasValue)
        {
            return null;
        }

        return JsonSerializer.Deserialize(payload.ToString(), ControlPlaneJsonContext.Default.ExecutionJobRecord);
    }

    public async Task SetAsync(
        ExecutionJobRecord job,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        var versioned = job with { Version = job.Version + 1 };
        var payload = JsonSerializer.Serialize(versioned, ControlPlaneJsonContext.Default.ExecutionJobRecord);
        await _database.StringSetAsync(
                GetJobKey(job.OperationId),
                payload,
                ttl ?? DefaultRetention)
            .ConfigureAwait(false);

        await UpdateActiveIndexesAsync(versioned).ConfigureAwait(false);
        Log.ExecutionJobUpdated(logger, job.OperationId, versioned.Status.ToString());
    }

    /// <inheritdoc />
    public async Task<bool> TrySetAsync(
        ExecutionJobRecord job,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        var versioned = job with { Version = job.Version + 1 };
        var payload = JsonSerializer.Serialize(versioned, ControlPlaneJsonContext.Default.ExecutionJobRecord);
        var retention = ttl ?? DefaultRetention;

        var result = await _database.ScriptEvaluateAsync(
            CasSetScript,
            keys: [(RedisKey)GetJobKey(job.OperationId)],
            values: [(RedisValue)job.Version, (RedisValue)payload, (RedisValue)(long)retention.TotalMilliseconds])
            .ConfigureAwait(false);

        if ((long)result != 1)
        {
            Log.CasConflict(logger, job.OperationId, job.Version);
            return false;
        }

        await UpdateActiveIndexesAsync(versioned).ConfigureAwait(false);
        Log.ExecutionJobUpdated(logger, job.OperationId, versioned.Status.ToString());
        return true;
    }

    public async Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(
        ExecutionJobKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var activeKey = kind.HasValue ? GetKindActiveKey(kind.Value) : ActiveJobsKey;
        var jobIds = await _database.SetMembersAsync(activeKey).ConfigureAwait(false);
        var jobs = new List<ExecutionJobRecord>(jobIds.Length);
        var staleIds = new List<RedisValue>();

        foreach (var jobId in jobIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!jobId.HasValue)
            {
                continue;
            }

            var job = await GetAsync(jobId.ToString(), cancellationToken).ConfigureAwait(false);
            if (job == null || IsTerminal(job.Status))
            {
                staleIds.Add(jobId);
                continue;
            }

            jobs.Add(job);
        }

        if (staleIds.Count > 0)
        {
            await RemoveStaleMembersAsync(activeKey, staleIds).ConfigureAwait(false);
        }

        return jobs
            .OrderByDescending(job => job.UpdatedAt)
            .ToArray();
    }

    private async Task UpdateActiveIndexesAsync(ExecutionJobRecord job)
    {
        var jobId = (RedisValue)job.OperationId;
        if (IsTerminal(job.Status))
        {
            await _database.SetRemoveAsync(ActiveJobsKey, jobId).ConfigureAwait(false);
            await _database.SetRemoveAsync(GetKindActiveKey(job.Spec.Kind), jobId).ConfigureAwait(false);
            return;
        }

        await _database.SetAddAsync(ActiveJobsKey, jobId).ConfigureAwait(false);
        await _database.SetAddAsync(GetKindActiveKey(job.Spec.Kind), jobId).ConfigureAwait(false);
    }

    private async Task RemoveStaleMembersAsync(string activeKey, IReadOnlyList<RedisValue> staleIds)
    {
        foreach (var staleId in staleIds)
        {
            await _database.SetRemoveAsync(activeKey, staleId).ConfigureAwait(false);
        }
    }

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;

    private static string GetJobKey(string operationId) => $"controlplane:job:{operationId}";

    private static string GetLeaseKey(string operationId) => $"controlplane:job:lease:{operationId}";

    private static string GetKindActiveKey(ExecutionJobKind kind)
        => $"controlplane:job:active:{kind.ToString().ToLowerInvariant()}";

    private const string ActiveJobsKey = "controlplane:job:active";

    private static partial class Log
    {
        [LoggerMessage(9010, LogLevel.Information, "Created execution job {OperationId} ({Kind}) with status {Status}")]
        public static partial void ExecutionJobCreated(
            ILogger logger,
            string operationId,
            string kind,
            string status);

        [LoggerMessage(9011, LogLevel.Debug, "Updated execution job {OperationId} to status {Status}")]
        public static partial void ExecutionJobUpdated(
            ILogger logger,
            string operationId,
            string status);

        [LoggerMessage(9012, LogLevel.Debug, "CAS conflict for execution job {OperationId} at version {Version}; concurrent write detected")]
        public static partial void CasConflict(
            ILogger logger,
            string operationId,
            long version);
    }
}
