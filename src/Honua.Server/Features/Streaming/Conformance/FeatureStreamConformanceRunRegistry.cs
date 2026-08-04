// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Streaming.Conformance;

/// <summary>
/// One leased conformance run.
/// </summary>
/// <remarks>
/// Budgets and owned-record bookkeeping live on the lease rather than being recomputed from
/// storage on every request: a bound that can be exceeded while a read is in flight is not a
/// bound. The authoritative ownership check still reads the record's stored marker before
/// any mutation, so the in-memory set can never authorize a write the row itself does not.
/// </remarks>
internal sealed class FeatureStreamConformanceRun
{
    private readonly HashSet<long> _ownedRecords = [];
    private readonly Lock _gate = new();
    private int _mutationsUsed;

    internal FeatureStreamConformanceRun(
        Guid runId,
        string token,
        string? clientLabel,
        DateTimeOffset expiresAt,
        int maxMutations,
        int maxRecords,
        string deploymentRevision)
    {
        RunId = runId;
        Token = token;
        ClientLabel = clientLabel;
        ExpiresAt = expiresAt;
        MaxMutations = maxMutations;
        MaxRecords = maxRecords;
        DeploymentRevision = deploymentRevision;
        Marker = new FeatureStreamConformanceMarker(runId, expiresAt).Format();
    }

    /// <summary>Run identifier.</summary>
    public Guid RunId { get; }

    /// <summary>Ownership token presented on every subsequent request.</summary>
    public string Token { get; }

    /// <summary>Caller-supplied label.</summary>
    public string? ClientLabel { get; }

    /// <summary>Absolute lease deadline.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Mutation budget.</summary>
    public int MaxMutations { get; }

    /// <summary>Concurrent controlled-record budget.</summary>
    public int MaxRecords { get; }

    /// <summary>Deployment revision this run is bound to.</summary>
    public string DeploymentRevision { get; }

    /// <summary>Ownership marker written to this run's controlled records.</summary>
    public string Marker { get; }

    /// <summary>Mutations performed so far.</summary>
    public int MutationsUsed
    {
        get
        {
            lock (_gate)
            {
                return _mutationsUsed;
            }
        }
    }

    /// <summary>Controlled records currently held.</summary>
    public int OwnedRecordCount
    {
        get
        {
            lock (_gate)
            {
                return _ownedRecords.Count;
            }
        }
    }

    /// <summary>Whether the lease deadline has passed.</summary>
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>
    /// Claims one unit of the mutation budget, and — for record-creating operations — one
    /// unit of the record budget. Returns the 1-based mutation ordinal, or null when the
    /// relevant budget is exhausted. Claiming before the write keeps a concurrent burst from
    /// overrunning the bound between the check and the edit.
    /// </summary>
    public int? TryClaimMutation(bool createsRecord)
    {
        lock (_gate)
        {
            if (_mutationsUsed >= MaxMutations)
            {
                return null;
            }

            if (createsRecord && _ownedRecords.Count >= MaxRecords)
            {
                return null;
            }

            _mutationsUsed++;
            return _mutationsUsed;
        }
    }

    /// <summary>Returns an unspent claim when the mutation itself failed.</summary>
    public void ReleaseClaim()
    {
        lock (_gate)
        {
            if (_mutationsUsed > 0)
            {
                _mutationsUsed--;
            }
        }
    }

    /// <summary>Records a newly created controlled record.</summary>
    public void TrackRecord(long objectId)
    {
        lock (_gate)
        {
            _ownedRecords.Add(objectId);
        }
    }

    /// <summary>Forgets a deleted controlled record.</summary>
    public void ForgetRecord(long objectId)
    {
        lock (_gate)
        {
            _ownedRecords.Remove(objectId);
        }
    }
}

/// <summary>
/// In-memory registry of leased conformance runs. Bounded by
/// <see cref="FeatureStreamConformanceOptions.MaxConcurrentRuns"/> and swept on TTL.
/// </summary>
/// <remarks>
/// Deliberately node-local. A conformance run holds a single HTTP conversation plus one or
/// two stream subscriptions against one deployment, and the durable half of the contract —
/// which records exist and who owns them — lives on the rows themselves, so a node-local
/// lease loses nothing that matters on failover: the TTL sweeper on any node reclaims the
/// records from their stored markers.
/// </remarks>
internal sealed class FeatureStreamConformanceRunRegistry
{
    private readonly ConcurrentDictionary<Guid, FeatureStreamConformanceRun> _runs = new();
    private readonly IOptions<FeatureStreamConformanceOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _leaseGate = new();

    public FeatureStreamConformanceRunRegistry(
        IOptions<FeatureStreamConformanceOptions> options,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Runs currently holding a lease.</summary>
    public int ActiveRunCount => _runs.Count;

    /// <summary>
    /// Projects the anonymous-safe capability advertisement. It lives on the registry rather
    /// than on the workflow service so the anonymous streaming-capabilities endpoint can
    /// advertise the contract from configuration and a counter alone — resolving the workflow
    /// there would drag the feature reader/writer into a discovery request that must stay
    /// cheap and must not fail on a deployment whose provider has no writer.
    /// </summary>
    public FeatureStreamConformanceCapability DescribeCapability()
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return new FeatureStreamConformanceCapability { Enabled = false };
        }

        return new FeatureStreamConformanceCapability
        {
            Enabled = true,
            ServiceId = options.ServiceId,
            LayerId = options.LayerId,
            RunIdField = options.RunIdField,
            MaxConcurrentRuns = options.MaxConcurrentRuns,
            ActiveRuns = _runs.Count,
            RunTtlSeconds = (int)options.RunTtl.TotalSeconds,
            MaxMutationsPerRun = options.MaxMutationsPerRun,
            MaxRecordsPerRun = options.MaxRecordsPerRun,
            Operations = FeatureStreamConformanceOperations.All
        };
    }

    /// <summary>
    /// Attempts to acquire a lease. Returns null when every lease is held, which is the
    /// fail-closed answer: a caller that cannot get an isolated run must report that it did
    /// not run, never mutate a shared source anyway.
    /// </summary>
    public FeatureStreamConformanceRun? TryLease(string? clientLabel, TimeSpan ttl, string deploymentRevision)
    {
        var options = _options.Value;
        var now = _timeProvider.GetUtcNow();

        lock (_leaseGate)
        {
            // Expired leases are reclaimed on the acquisition path as well as by the sweeper
            // so a run is never refused because of a lease nobody is using any more.
            foreach (var candidate in _runs.Values)
            {
                if (candidate.IsExpired(now))
                {
                    _runs.TryRemove(candidate.RunId, out _);
                }
            }

            if (_runs.Count >= options.MaxConcurrentRuns)
            {
                return null;
            }

            var run = new FeatureStreamConformanceRun(
                Guid.NewGuid(),
                CreateToken(),
                clientLabel,
                now.Add(ttl),
                options.MaxMutationsPerRun,
                options.MaxRecordsPerRun,
                deploymentRevision);

            return _runs.TryAdd(run.RunId, run) ? run : null;
        }
    }

    /// <summary>
    /// Resolves a live run by identity and token. Returns null for an unknown run, an
    /// expired run, or a token mismatch — all indistinguishable to the caller on purpose, so
    /// the surface cannot be used to confirm that another run exists.
    /// </summary>
    public FeatureStreamConformanceRun? Resolve(Guid runId, string? token)
    {
        if (!_runs.TryGetValue(runId, out var run))
        {
            return null;
        }

        if (run.IsExpired(_timeProvider.GetUtcNow()))
        {
            return null;
        }

        return TokenMatches(run.Token, token) ? run : null;
    }

    /// <summary>Drops a lease. Idempotent.</summary>
    public void Release(Guid runId) => _runs.TryRemove(runId, out _);

    /// <summary>Drops every lease and returns how many were held.</summary>
    public int ReleaseAll()
    {
        var released = _runs.Count;
        _runs.Clear();
        return released;
    }

    /// <summary>Removes and returns every lease whose deadline has passed.</summary>
    public IReadOnlyList<FeatureStreamConformanceRun> ReclaimExpired()
    {
        var now = _timeProvider.GetUtcNow();
        List<FeatureStreamConformanceRun>? expired = null;
        foreach (var run in _runs.Values)
        {
            if (!run.IsExpired(now))
            {
                continue;
            }

            if (_runs.TryRemove(run.RunId, out var removed))
            {
                (expired ??= []).Add(removed);
            }
        }

        return expired ?? (IReadOnlyList<FeatureStreamConformanceRun>)[];
    }

    /// <summary>Whether a run id currently holds a lease, regardless of token.</summary>
    public bool IsLeased(Guid runId)
        => _runs.TryGetValue(runId, out var run) && !run.IsExpired(_timeProvider.GetUtcNow());

    private static string CreateToken()
        => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Constant-time token comparison so a caller cannot recover another run's token by
    /// timing its rejection.
    /// </summary>
    private static bool TokenMatches(string expected, string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        var presentedBytes = System.Text.Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
    }
}
