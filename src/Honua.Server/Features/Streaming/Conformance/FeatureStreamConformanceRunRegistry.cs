// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Streaming.Conformance;

// The in-memory set never authorizes a write the stored marker does not.
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

    public Guid RunId { get; }

    public string Token { get; }

    public string? ClientLabel { get; }

    public DateTimeOffset ExpiresAt { get; }

    public int MaxMutations { get; }

    public int MaxRecords { get; }

    public string DeploymentRevision { get; }

    public string Marker { get; }

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

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

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

    public void TrackRecord(long objectId)
    {
        lock (_gate)
        {
            _ownedRecords.Add(objectId);
        }
    }

    public void ForgetRecord(long objectId)
    {
        lock (_gate)
        {
            _ownedRecords.Remove(objectId);
        }
    }
}

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

    public int ActiveRunCount => _runs.Count;

    // Anonymous capability discovery reads this registry so it does not resolve the workflow and fail on a provider with no writer.
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

    // When every lease is held the caller must report that it did not run, and it must not mutate a shared source.
    public FeatureStreamConformanceRun? TryLease(string? clientLabel, TimeSpan ttl, string deploymentRevision)
    {
        var options = _options.Value;
        var now = _timeProvider.GetUtcNow();

        lock (_leaseGate)
        {
            // Expired leases are reclaimed on the acquisition path as well as by the sweeper
            // so a run is never refused because of a lease nobody is using any more.
            // `Values` materializes a snapshot, so removing while enumerating the filtered
            // view is safe.
            foreach (var candidate in _runs.Values.Where(candidate => candidate.IsExpired(now)))
            {
                _runs.TryRemove(candidate.RunId, out _);
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

    // Unknown, expired, and mismatched tokens are indistinguishable, so the surface cannot confirm another run.
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

    public void Release(Guid runId) => _runs.TryRemove(runId, out _);

    public int ReleaseAll()
    {
        var released = _runs.Count;
        _runs.Clear();
        return released;
    }

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

    public bool IsLeased(Guid runId)
        => _runs.TryGetValue(runId, out var run) && !run.IsExpired(_timeProvider.GetUtcNow());

    private static string CreateToken()
        => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    // Comparison is constant-time so a caller cannot recover a token by timing.
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
