// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Coordination;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

// The alert evaluator leads on a lease row in the system database. The alert stores live in that
// database, so a host without one cannot evaluate: it never leads and reports every attempt as a fault.
internal sealed partial class LeaseLeaderElectionStrategy : ILeaderElectionStrategy
{
    internal const string LeadershipKey = "alerts:evaluation:leader";

    private readonly LeaseLeaderElection? _election;
    private readonly ILogger<LeaseLeaderElectionStrategy> _logger;

    public LeaseLeaderElectionStrategy(
        ILeaseStore? leases,
        IOptions<AlertOptions> options,
        ILogger<LeaseLeaderElectionStrategy> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _election = leases is null
            ? null
            : new LeaseLeaderElection(leases, LeadershipKey, options.Value.Evaluation.LeaderLeaseDuration);
        InstanceId = _election?.InstanceId ?? LeaseLeaderElection.NodeId;
    }

    public bool IsLeader => _election?.IsLeader ?? false;

    public bool LastAcquireFaulted { get; private set; }

    public string InstanceId { get; }

    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        if (_election is null)
        {
            LastAcquireFaulted = true;
            return false;
        }

        try
        {
            var leads = await _election.TryAcquireLeadershipAsync(cancellationToken).ConfigureAwait(false);
            LastAcquireFaulted = false;
            return leads;
        }
        // Intentional catch-all: the store could not answer, so nobody can tell whether a leader
        // exists. Flag the fault so the evaluation loop surfaces a no-leader stall.
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            LastAcquireFaulted = true;
            LogAcquireFailed(_logger, ex);
            return false;
        }
    }

    public Task ReleaseAsync(CancellationToken cancellationToken = default)
        => _election?.ReleaseLeadershipAsync(cancellationToken) ?? Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to acquire the alert evaluator leader lease.")]
    private static partial void LogAcquireFailed(ILogger logger, Exception exception);
}
