// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using StackExchange.Redis;

namespace Honua.ControlPlane;

/// <summary>
/// Redis-backed durable store for operation proposals awaiting human approval.
/// Mirrors <c>RedisWorkflowOperationStore</c>: TTL retention, optimistic version
/// tokens, reconciliation leases, and an active-by-kind index (#1692).
/// </summary>
internal sealed partial class RedisOperationProposalStore(
    IConnectionMultiplexer redis,
    ILogger<RedisOperationProposalStore> logger) : IOperationProposalStore
{
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);
    private const string ActiveProposalsKey = "controlplane:proposal:active";

    // Compare-and-set: write only when the stored version matches the expected
    // version, then persist the incremented record. Returns 1 on success, 0 on
    // a version conflict (including a missing key when an update was expected).
    private const string CompareAndSetScript = """
        local current = redis.call('GET', KEYS[1])
        if current == false then
            if tonumber(ARGV[2]) ~= -1 then
                return 0
            end
        else
            local decoded = cjson.decode(current)
            if tonumber(decoded.version) ~= tonumber(ARGV[2]) then
                return 0
            end
        end
        redis.call('SET', KEYS[1], ARGV[1], 'PX', tonumber(ARGV[3]))
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
        OperationProposal proposal,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();

        var record = proposal with { Version = 0 };
        var created = await PersistCreateAsync(record, ttl ?? DefaultRetention).ConfigureAwait(false);
        if (!created)
        {
            return false;
        }

        Log.ProposalCreated(logger, record.ProposalId, record.Kind.ToString(), record.Status.ToString());
        return true;
    }

    public async Task<OperationProposal?> GetAsync(
        string proposalId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(proposalId))
        {
            return null;
        }

        var payload = await _database.StringGetAsync(GetProposalKey(proposalId)).ConfigureAwait(false);
        if (!payload.HasValue)
        {
            return null;
        }

        return JsonSerializer.Deserialize(payload.ToString(), OperationProposalJsonContext.Default.OperationProposal);
    }

    public async Task<bool> TrySetAsync(
        OperationProposal proposal,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();

        var expectedVersion = proposal.Version;
        var next = proposal with { Version = expectedVersion + 1, UpdatedAt = DateTimeOffset.UtcNow };
        var payload = JsonSerializer.Serialize(next, OperationProposalJsonContext.Default.OperationProposal);
        var retention = ttl ?? DefaultRetention;

        var result = await _database.ScriptEvaluateAsync(
            CompareAndSetScript,
            [(RedisKey)GetProposalKey(next.ProposalId)],
            [(RedisValue)payload, (RedisValue)expectedVersion, (RedisValue)(long)retention.TotalMilliseconds])
            .ConfigureAwait(false);

        if ((int)result != 1)
        {
            Log.ProposalVersionConflict(logger, next.ProposalId, expectedVersion);
            return false;
        }

        await UpdateActiveIndexAsync(next).ConfigureAwait(false);
        Log.ProposalUpdated(logger, next.ProposalId, next.Status.ToString());
        return true;
    }

    public async Task<IReadOnlyList<OperationProposal>> ListActiveAsync(
        OperationClass? kind = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var activeKey = kind.HasValue ? GetKindActiveKey(kind.Value) : ActiveProposalsKey;
        var proposalIds = await _database.SetMembersAsync(activeKey).ConfigureAwait(false);
        var proposals = new List<OperationProposal>(proposalIds.Length);
        var staleIds = new List<RedisValue>();

        foreach (var proposalId in proposalIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!proposalId.HasValue)
            {
                continue;
            }

            var proposal = await GetAsync(proposalId.ToString(), cancellationToken).ConfigureAwait(false);
            if (proposal == null || IsTerminal(proposal.Status))
            {
                staleIds.Add(proposalId);
                continue;
            }

            proposals.Add(proposal);
        }

        foreach (var staleId in staleIds)
        {
            await _database.SetRemoveAsync(activeKey, staleId).ConfigureAwait(false);
        }

        return proposals
            .OrderByDescending(proposal => proposal.UpdatedAt)
            .ToArray();
    }

    private async Task<bool> PersistCreateAsync(OperationProposal proposal, TimeSpan retention)
    {
        var key = GetProposalKey(proposal.ProposalId);
        var transaction = _database.CreateTransaction();
        transaction.AddCondition(Condition.KeyNotExists(key));

        var payload = JsonSerializer.Serialize(proposal, OperationProposalJsonContext.Default.OperationProposal);
        var writeTask = transaction.StringSetAsync(key, payload, retention);
        if (!IsTerminal(proposal.Status))
        {
            _ = transaction.SetAddAsync(ActiveProposalsKey, proposal.ProposalId);
            _ = transaction.SetAddAsync(GetKindActiveKey(proposal.Kind), proposal.ProposalId);
        }

        var committed = await transaction.ExecuteAsync().ConfigureAwait(false);
        if (!committed)
        {
            return false;
        }

        await writeTask.ConfigureAwait(false);
        return true;
    }

    private async Task UpdateActiveIndexAsync(OperationProposal proposal)
    {
        var id = (RedisValue)proposal.ProposalId;
        if (IsTerminal(proposal.Status))
        {
            await _database.SetRemoveAsync(ActiveProposalsKey, id).ConfigureAwait(false);
            await _database.SetRemoveAsync(GetKindActiveKey(proposal.Kind), id).ConfigureAwait(false);
            return;
        }

        await _database.SetAddAsync(ActiveProposalsKey, id).ConfigureAwait(false);
        await _database.SetAddAsync(GetKindActiveKey(proposal.Kind), id).ConfigureAwait(false);
    }

    private static bool IsTerminal(OperationProposalStatus status)
        => status is OperationProposalStatus.Succeeded
            or OperationProposalStatus.Failed
            or OperationProposalStatus.Rejected
            or OperationProposalStatus.RolledBack;

    private static string GetProposalKey(string proposalId) => $"controlplane:proposal:{proposalId}";

    private static string GetLeaseKey(string proposalId) => $"controlplane:proposal:lease:{proposalId}";

    private static string GetKindActiveKey(OperationClass kind)
        => $"controlplane:proposal:active:{kind.ToString().ToLowerInvariant()}";

    private static partial class Log
    {
        [LoggerMessage(9100, LogLevel.Information, "Created operation proposal {ProposalId} ({Kind}) with status {Status}")]
        public static partial void ProposalCreated(ILogger logger, string proposalId, string kind, string status);

        [LoggerMessage(9101, LogLevel.Debug, "Updated operation proposal {ProposalId} to status {Status}")]
        public static partial void ProposalUpdated(ILogger logger, string proposalId, string status);

        [LoggerMessage(9102, LogLevel.Debug, "Optimistic version conflict updating proposal {ProposalId} (expected {ExpectedVersion})")]
        public static partial void ProposalVersionConflict(ILogger logger, string proposalId, long expectedVersion);
    }
}
