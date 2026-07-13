// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using StackExchange.Redis;

namespace Honua.ControlPlane;

/// <summary>
/// Redis-backed durable store for run-to-completion execution jobs and reconciliation leases.
/// </summary>
internal sealed partial class RedisExecutionJobStore(
    IConnectionMultiplexer redis,
    ILogger<RedisExecutionJobStore> logger) : IExecutionJobStore
{
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);
    private const int MinQueryLimit = 1;
    private const int MaxQueryLimit = 200;
    private const int QueryBatchSize = 256;

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

        await UpdateIndexesAsync(previous: null, versioned).ConfigureAwait(false);
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

        var previous = await GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
        var versioned = job with { Version = job.Version + 1 };
        var payload = JsonSerializer.Serialize(versioned, ControlPlaneJsonContext.Default.ExecutionJobRecord);
        await _database.StringSetAsync(
                GetJobKey(job.OperationId),
                payload,
                ttl ?? DefaultRetention)
            .ConfigureAwait(false);

        await UpdateIndexesAsync(previous, versioned).ConfigureAwait(false);
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

        var previous = await GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
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

        await UpdateIndexesAsync(previous, versioned).ConfigureAwait(false);
        Log.ExecutionJobUpdated(logger, job.OperationId, versioned.Status.ToString());
        return true;
    }

    public async Task<ExecutionJobPage> QueryAsync(
        ExecutionJobQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryDecodeJobCursor(query.Cursor, out var cursor, out var cursorError))
        {
            throw new ArgumentException(cursorError, nameof(query));
        }

        var limit = Math.Clamp(query.Limit, MinQueryLimit, MaxQueryLimit);
        var baseKey = SelectQueryIndexKey(query);
        var minScore = query.CreatedFrom.HasValue
            ? query.CreatedFrom.Value.ToUnixTimeMilliseconds()
            : double.NegativeInfinity;
        var maxScore = ResolveMaxScore(query, cursor);

        if (maxScore < minScore)
        {
            return new ExecutionJobPage { Items = Array.Empty<ExecutionJobRecord>() };
        }

        var results = new List<ExecutionJobRecord>(limit + 1);
        var staleMembers = new List<RedisValue>();
        var scoreWindowMax = maxScore;
        long scoreWindowSkip = 0;

        while (results.Count <= limit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entries = await _database.SortedSetRangeByScoreWithScoresAsync(
                    baseKey,
                    minScore,
                    scoreWindowMax,
                    Exclude.None,
                    Order.Descending,
                    skip: scoreWindowSkip,
                    take: QueryBatchSize)
                .ConfigureAwait(false);

            if (entries.Length == 0)
            {
                break;
            }

            var lowestScore = (long)entries[^1].Score;
            var processedAtLowestScore = 0;

            // Collect candidates that pass the cursor filter, then batch-fetch with MGET.
            var candidates = new List<(SortedSetEntry Entry, string JobId)>(entries.Length);
            foreach (var entry in entries)
            {
                var jobId = entry.Element.ToString();
                var score = (long)entry.Score;
                if (score == lowestScore)
                {
                    processedAtLowestScore++;
                }

                if (cursor is { } cursorValue && IsAtOrBeforeCursor(score, jobId, cursorValue))
                {
                    continue;
                }

                candidates.Add((entry, jobId));
            }

            if (candidates.Count > 0)
            {
                var keys = candidates.ConvertAll(c => (RedisKey)GetJobKey(c.JobId));
                var payloads = await _database.StringGetAsync([.. keys]).ConfigureAwait(false);

                for (var i = 0; i < candidates.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var (entry, jobId) = candidates[i];
                    var payload = payloads[i];
                    if (!payload.HasValue)
                    {
                        staleMembers.Add(entry.Element);
                        continue;
                    }

                    var job = JsonSerializer.Deserialize(payload.ToString(), ControlPlaneJsonContext.Default.ExecutionJobRecord);
                    if (job == null)
                    {
                        staleMembers.Add(entry.Element);
                        continue;
                    }

                    if (MatchesQuery(query, job))
                    {
                        results.Add(job);
                        if (results.Count > limit)
                        {
                            break;
                        }
                    }
                }
            }

            if (results.Count > limit)
            {
                break;
            }

            if (entries.Length < QueryBatchSize)
            {
                break;
            }

            // Both sides are always integer-valued: scores are written as
            // CreatedAt.ToUnixTimeMilliseconds() (see UpdateQueryIndexesAsync) and never carry a
            // fractional tie-breaker, and millisecond epoch values stay well within double's
            // 53-bit exact-integer range. This equality check has no floating-point precision risk.
            if (lowestScore == scoreWindowMax)
            {
                scoreWindowSkip += processedAtLowestScore;
            }
            else
            {
                scoreWindowMax = lowestScore;
                scoreWindowSkip = processedAtLowestScore;
            }
        }

        if (staleMembers.Count > 0)
        {
            await RemoveStaleSortedMembersAsync(baseKey, staleMembers).ConfigureAwait(false);
        }

        string? nextCursor = null;
        if (results.Count > limit)
        {
            var lastReturned = results[limit - 1];
            nextCursor = EncodeJobCursor(lastReturned.CreatedAt.ToUnixTimeMilliseconds(), lastReturned.OperationId);
            results.RemoveAt(results.Count - 1);
        }

        return new ExecutionJobPage
        {
            Items = results,
            NextCursor = nextCursor
        };
    }

    public async Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(
        ExecutionJobKind? kind = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var activeKey = kind.HasValue ? GetKindActiveKey(kind.Value) : ActiveJobsKey;
        var jobIds = await _database.SetMembersAsync(activeKey).ConfigureAwait(false);

        // BH7-009: When a limit is supplied, cap the number of IDs we materialise.
        // Apply a 2x overfetch so per-job authorization filters can discard some
        // records without starving the requested page. Redis Sets have no stable
        // order; the subset is best-effort.
        var maxIds = limit.HasValue ? limit.Value * 2 : int.MaxValue;

        var validIds = new List<RedisValue>(Math.Min(jobIds.Length, maxIds));
        foreach (var jobId in jobIds)
        {
            if (validIds.Count >= maxIds)
            {
                break;
            }

            if (jobId.HasValue)
            {
                validIds.Add(jobId);
            }
        }

        var jobs = new List<ExecutionJobRecord>(validIds.Count);
        var staleIds = new List<RedisValue>();

        if (validIds.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var keys = validIds.ConvertAll(id => (RedisKey)GetJobKey(id.ToString()!));
            var payloads = await _database.StringGetAsync([.. keys]).ConfigureAwait(false);

            for (var i = 0; i < validIds.Count; i++)
            {
                var payload = payloads[i];
                if (!payload.HasValue)
                {
                    staleIds.Add(validIds[i]);
                    continue;
                }

                var job = JsonSerializer.Deserialize(payload.ToString(), ControlPlaneJsonContext.Default.ExecutionJobRecord);
                if (job == null || IsTerminal(job.Status))
                {
                    staleIds.Add(validIds[i]);
                    continue;
                }

                jobs.Add(job);
            }
        }

        if (staleIds.Count > 0)
        {
            await RemoveStaleMembersAsync(activeKey, staleIds).ConfigureAwait(false);
        }

        return jobs
            .OrderByDescending(job => job.UpdatedAt)
            .ToArray();
    }

    private async Task UpdateIndexesAsync(ExecutionJobRecord? previous, ExecutionJobRecord job)
    {
        await UpdateActiveIndexesAsync(job).ConfigureAwait(false);
        await UpdateQueryIndexesAsync(previous, job).ConfigureAwait(false);
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

    private async Task UpdateQueryIndexesAsync(ExecutionJobRecord? previous, ExecutionJobRecord job)
    {
        var jobId = (RedisValue)job.OperationId;

        // Each key's remove/add targets an independent sorted set, so within a phase
        // the calls have no ordering dependency on one another and can be dispatched
        // concurrently (StackExchange.Redis pipelines them over the shared
        // multiplexer connection instead of issuing ~30 sequential round trips).
        // The removal phase still fully completes before the addition phase starts,
        // preserving remove-before-add ordering for any key present in both sets.
        if (previous != null)
        {
            var previousKeys = GetQueryIndexKeys(previous);
            await Task.WhenAll(previousKeys.Select(key => _database.SortedSetRemoveAsync(key, jobId)))
                .ConfigureAwait(false);
        }

        var score = job.CreatedAt.ToUnixTimeMilliseconds();
        var newKeys = GetQueryIndexKeys(job);
        await Task.WhenAll(newKeys.Select(key => _database.SortedSetAddAsync(key, jobId, score)))
            .ConfigureAwait(false);
    }

    private async Task RemoveStaleMembersAsync(string activeKey, IReadOnlyList<RedisValue> staleIds)
    {
        // SREM accepts a variadic member list, so the whole batch removes in a
        // single round trip instead of one RTT per stale member.
        await _database.SetRemoveAsync(activeKey, [.. staleIds]).ConfigureAwait(false);
    }

    private async Task RemoveStaleSortedMembersAsync(string key, IReadOnlyList<RedisValue> staleIds)
    {
        // ZREM accepts a variadic member list; see RemoveStaleMembersAsync above.
        await _database.SortedSetRemoveAsync(key, [.. staleIds]).ConfigureAwait(false);
    }

    private static HashSet<string> GetQueryIndexKeys(ExecutionJobRecord job)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal)
        {
            CreatedAllIndexKey,
            GetStatusIndexKey(job.Status),
            GetKindIndexKey(job.Spec.Kind)
        };

        AddHashedKey(keys, "backend", job.Spec.Backend);
        AddHashedKey(keys, "queue", ExecutionJobMetadata.ResolveQueue(job));
        AddHashedKey(keys, "requested-by", job.Audit.RequestedBy);
        AddHashedKey(keys, "correlation", job.Audit.CorrelationId);
        AddMetadataKey(keys, "trace", job, ExecutionJobParameterKeys.TraceId);
        AddMetadataKey(keys, "definition", job, ExecutionJobParameterKeys.DefinitionId);
        AddMetadataKey(keys, "definition", job, ExecutionJobParameterKeys.GeoprocessingPlanId);
        AddMetadataKey(keys, "environment", job, ExecutionJobParameterKeys.Environment);
        AddMetadataKey(keys, "server", job, ExecutionJobParameterKeys.Server);
        AddMetadataKey(keys, "release", job, ExecutionJobParameterKeys.ReleaseId);
        AddMetadataKey(keys, "change-set", job, ExecutionJobParameterKeys.ChangeSetId);
        AddMetadataKey(keys, "alert", job, ExecutionJobParameterKeys.AlertId);

        foreach (var resourceRef in GetResourceRefs(job))
        {
            AddHashedKey(keys, "resource", resourceRef);
        }

        return keys;
    }

    private static string SelectQueryIndexKey(ExecutionJobQuery query)
    {
        if (query.Statuses.Count == 1)
        {
            return GetStatusIndexKey(query.Statuses[0]);
        }

        if (query.Kind.HasValue)
        {
            return GetKindIndexKey(query.Kind.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Backend))
        {
            return GetHashedIndexKey("backend", query.Backend);
        }

        if (!string.IsNullOrWhiteSpace(query.Queue))
        {
            return GetHashedIndexKey("queue", query.Queue);
        }

        if (!string.IsNullOrWhiteSpace(query.RequestedBy))
        {
            return GetHashedIndexKey("requested-by", query.RequestedBy);
        }

        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
        {
            return GetHashedIndexKey("correlation", query.CorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(query.TraceId))
        {
            return GetHashedIndexKey("trace", query.TraceId);
        }

        if (!string.IsNullOrWhiteSpace(query.DefinitionId))
        {
            return GetHashedIndexKey("definition", query.DefinitionId);
        }

        if (!string.IsNullOrWhiteSpace(query.ResourceRef))
        {
            return GetHashedIndexKey("resource", query.ResourceRef);
        }

        if (!string.IsNullOrWhiteSpace(query.Environment))
        {
            return GetHashedIndexKey("environment", query.Environment);
        }

        if (!string.IsNullOrWhiteSpace(query.Server))
        {
            return GetHashedIndexKey("server", query.Server);
        }

        if (!string.IsNullOrWhiteSpace(query.ReleaseId))
        {
            return GetHashedIndexKey("release", query.ReleaseId);
        }

        if (!string.IsNullOrWhiteSpace(query.ChangeSetId))
        {
            return GetHashedIndexKey("change-set", query.ChangeSetId);
        }

        if (!string.IsNullOrWhiteSpace(query.AlertId))
        {
            return GetHashedIndexKey("alert", query.AlertId);
        }

        return CreatedAllIndexKey;
    }

    private static bool MatchesQuery(ExecutionJobQuery query, ExecutionJobRecord job)
    {
        if (query.Statuses.Count > 0 && !query.Statuses.Contains(job.Status))
        {
            return false;
        }

        if (query.Kind.HasValue && job.Spec.Kind != query.Kind.Value)
        {
            return false;
        }

        if (query.CreatedFrom is { } from && job.CreatedAt < from)
        {
            return false;
        }

        if (query.CreatedTo is { } to && job.CreatedAt >= to)
        {
            return false;
        }

        return Matches(query.Backend, job.Spec.Backend)
            && Matches(query.Queue, ExecutionJobMetadata.ResolveQueue(job))
            && Matches(query.RequestedBy, job.Audit.RequestedBy)
            && Matches(query.CorrelationId, job.Audit.CorrelationId)
            && Matches(query.TraceId, GetParameter(job, ExecutionJobParameterKeys.TraceId))
            && MatchesDefinition(query.DefinitionId, job)
            && MatchesAny(query.ResourceRef, GetResourceRefs(job))
            && Matches(query.Environment, GetParameter(job, ExecutionJobParameterKeys.Environment))
            && Matches(query.Server, GetParameter(job, ExecutionJobParameterKeys.Server))
            && Matches(query.ReleaseId, GetParameter(job, ExecutionJobParameterKeys.ReleaseId))
            && Matches(query.ChangeSetId, GetParameter(job, ExecutionJobParameterKeys.ChangeSetId))
            && Matches(query.AlertId, GetParameter(job, ExecutionJobParameterKeys.AlertId));
    }

    private static bool MatchesDefinition(string? expected, ExecutionJobRecord job)
        => Matches(expected, GetParameter(job, ExecutionJobParameterKeys.DefinitionId))
            || Matches(expected, GetParameter(job, ExecutionJobParameterKeys.GeoprocessingPlanId));

    private static bool MatchesAny(string? expected, IReadOnlyList<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        foreach (var candidate in candidates)
        {
            if (Matches(expected, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(string? expected, string? actual)
        => string.IsNullOrWhiteSpace(expected)
            || string.Equals(expected.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static double ResolveMaxScore(ExecutionJobQuery query, JobCursor? cursor)
    {
        var maxScore = query.CreatedTo.HasValue
            ? query.CreatedTo.Value.ToUnixTimeMilliseconds()
            : double.PositiveInfinity;
        if (cursor.HasValue)
        {
            maxScore = Math.Min(maxScore, cursor.Value.CreatedAtUnixMilliseconds);
        }

        return maxScore;
    }

    private static bool IsAtOrBeforeCursor(long score, string jobId, JobCursor cursor)
    {
        if (score > cursor.CreatedAtUnixMilliseconds)
        {
            return true;
        }

        if (score < cursor.CreatedAtUnixMilliseconds)
        {
            return false;
        }

        return string.CompareOrdinal(jobId, cursor.JobId) >= 0;
    }

    private static void AddMetadataKey(HashSet<string> keys, string dimension, ExecutionJobRecord job, string parameterKey)
        => AddHashedKey(keys, dimension, GetParameter(job, parameterKey));

    private static void AddHashedKey(HashSet<string> keys, string dimension, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        keys.Add(GetHashedIndexKey(dimension, value));
    }

    private static string[] GetResourceRefs(ExecutionJobRecord job)
    {
        var raw = GetParameter(job, ExecutionJobParameterKeys.ResourceRefs);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw.Split(
                ExecutionJobParameterKeys.MetadataListSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? GetParameter(ExecutionJobRecord job, string key)
        => ExecutionJobMetadata.GetParameter(job, key);

    private static bool TryDecodeJobCursor(string? cursor, out JobCursor? value, out string error)
    {
        value = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var separator = decoded.IndexOf('|', StringComparison.Ordinal);
            if (separator <= 0 || separator == decoded.Length - 1)
            {
                error = "Invalid job cursor.";
                return false;
            }

            if (!long.TryParse(decoded[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var createdAtMs))
            {
                error = "Invalid job cursor.";
                return false;
            }

            value = new JobCursor(createdAtMs, decoded[(separator + 1)..]);
            return true;
        }
        catch (FormatException)
        {
            error = "Invalid job cursor.";
            return false;
        }
    }

    private static string EncodeJobCursor(long createdAtUnixMilliseconds, string jobId)
    {
        var value = string.Concat(createdAtUnixMilliseconds.ToString(CultureInfo.InvariantCulture), "|", jobId);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;

    private static string GetJobKey(string operationId) => $"controlplane:job:{operationId}";

    private static string GetLeaseKey(string operationId) => $"controlplane:job:lease:{operationId}";

    private static string GetKindActiveKey(ExecutionJobKind kind)
        => $"controlplane:job:active:{kind.ToString().ToLowerInvariant()}";

    private static string GetStatusIndexKey(ExecutionJobStatus status)
        => $"controlplane:job:index:created:status:{status.ToString().ToLowerInvariant()}";

    private static string GetKindIndexKey(ExecutionJobKind kind)
        => $"controlplane:job:index:created:kind:{kind.ToString().ToLowerInvariant()}";

    private static string GetHashedIndexKey(string dimension, string value)
        => $"controlplane:job:index:created:{dimension}:{HashIndexValue(value)}";

    private static string HashIndexValue(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private const string ActiveJobsKey = "controlplane:job:active";
    private const string CreatedAllIndexKey = "controlplane:job:index:created:all";

    private readonly record struct JobCursor(long CreatedAtUnixMilliseconds, string JobId);

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
