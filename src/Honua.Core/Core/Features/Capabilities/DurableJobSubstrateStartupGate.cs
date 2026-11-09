// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

// Entitlement decides whether the durable job substrate is composed; durability attestation only decides what the server advertises.
public sealed class DurableJobSubstrateEntitlement
{
    public const string CapabilityId = CapabilityUnavailableCodes.DurableJobsCapability;
}

public sealed class JobDurabilityOptions
{
    public const string SectionName = "Jobs";

    // Requiring a CONFIG GET attestation cannot be satisfied on managed Redis that blocks CONFIG.
    public bool RequireDurableStore { get; set; }
}

public sealed class DurableJobSubstrateNotAttestedException : Exception
{
    public DurableJobSubstrateNotAttestedException()
        : this(DurableJobSubstrateCause.RedisAttestationUnavailable, detail: null)
    {
    }

    public DurableJobSubstrateNotAttestedException(string message)
        : base(message)
    {
        Cause = DurableJobSubstrateCause.RedisAttestationUnavailable;
        Remediation = DurableJobSubstrateRemediation.For(Cause);
    }

    public DurableJobSubstrateNotAttestedException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Cause = DurableJobSubstrateCause.RedisAttestationUnavailable;
        Remediation = DurableJobSubstrateRemediation.For(Cause);
    }

    public DurableJobSubstrateNotAttestedException(DurableJobSubstrateCause cause, string? detail)
        : base(BuildMessage(cause, detail))
    {
        Cause = cause;
        Detail = detail;
        Remediation = DurableJobSubstrateRemediation.For(cause);
    }

    public DurableJobSubstrateCause Cause { get; }

    public string? Detail { get; }

    public string Remediation { get; } = string.Empty;

    public const string CapabilityId = CapabilityUnavailableCodes.DurableJobsCapability;

    private static string BuildMessage(DurableJobSubstrateCause cause, string? detail)
        => $"{JobDurabilityOptions.SectionName}:{nameof(JobDurabilityOptions.RequireDurableStore)} is enabled, "
            + $"but the durable job substrate is not attested ({cause}"
            + (string.IsNullOrWhiteSpace(detail) ? ")" : $": {detail})")
            + $". {DurableJobSubstrateRemediation.For(cause)} "
            + $"Alternatively, clear {JobDurabilityOptions.SectionName}:{nameof(JobDurabilityOptions.RequireDurableStore)} "
            + "to treat the attestation as information (the default; required on managed Redis that "
            + "blocks CONFIG, such as AWS ElastiCache and MemoryDB).";
}

public static class DurableJobSubstrateRemediation
{
    public const string NonDurableConsequence =
        "The governed control plane and the durable job runner stay enabled (operations.proposals "
        + "and jobs.runner are advertised); durability is an operator property of the Redis "
        + "deployment, and the capability manifest publishes this outcome as "
        + "limits.job.redisDurability.";

    public static string For(DurableJobSubstrateCause cause)
        => cause switch
        {
            DurableJobSubstrateCause.Available =>
                "No action required; Redis durability is attested.",

            DurableJobSubstrateCause.RedisPersistenceDisabled =>
                "Enable Redis append-only persistence (start Redis with "
                + "'redis-server --appendonly yes', or set 'appendonly yes' in redis.conf) so "
                + "acknowledged control-plane writes survive a restart.",

            DurableJobSubstrateCause.RedisWritePolicyUnsafe =>
                "Set the Redis 'appendfsync' policy to 'everysec' or 'always'; under 'no' an "
                + "acknowledged write can still be lost in the OS page cache.",

            DurableJobSubstrateCause.RedisEvictionPolicyUnsafe =>
                "Set the Redis 'maxmemory-policy' to 'noeviction' so durable control-plane keys "
                + "are never evicted under memory pressure.",

            DurableJobSubstrateCause.RedisAttestationUnavailable =>
                "No action is required on managed Redis that blocks CONFIG (AWS ElastiCache, "
                + "MemoryDB): rely on the service's own durability (replication, snapshots, "
                + "Multi-AZ, MemoryDB's transaction log). On self-managed Redis, let the server's "
                + "Redis user read the persistence policy ('INFO persistence' plus 'CONFIG GET' "
                + "for appendonly, appendfsync and maxmemory-policy) to have it attested.",

            DurableJobSubstrateCause.RedisUnreachable =>
                "Redis is configured but did not connect at startup. Check the 'redis' "
                + "connection string, network reachability, TLS and credentials, then restart.",

            DurableJobSubstrateCause.RedisNotConfigured =>
                CapabilityUnavailableCodes.RedisRemediation,

            DurableJobSubstrateCause.RedisNotEntitled =>
                CapabilityUnavailableCodes.EntitlementRemediation,

            DurableJobSubstrateCause.RuntimeIncomplete =>
                CapabilityUnavailableCodes.RuntimeIncompleteRemediation,

            _ => CapabilityUnavailableCodes.RedisRemediation,
        };
}

public static class DurableJobSubstrateStartupGate
{
    public static void EnsureSatisfied(
        DurableJobSubstrateOptions options,
        bool requireDurableStore,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!requireDurableStore || options.RedisDurabilityAttestation is not null)
        {
            return;
        }

        // The configuration facts decide first (absent, unentitled, unreachable Redis);
        // jobStorePresent/jobQueuePresent are not knowable before the provider is built and are
        // not what decides this. For a connected, entitled Redis report the attestation outcome
        // itself (unverified / not-durable) rather than a generic incomplete runtime.
        var cause = options.Classify(jobStorePresent: false, jobQueuePresent: false);
        if (cause == DurableJobSubstrateCause.RuntimeIncomplete && options.RedisDurabilityFailure is { } outcome)
        {
            cause = outcome;
        }

        throw new DurableJobSubstrateNotAttestedException(cause, detail);
    }
}
