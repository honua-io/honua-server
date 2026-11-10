// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

// The remediation differs when Redis is missing and when Redis is present but the licence does not entitle it.
public enum DurableJobSubstrateCause
{
    Available,

    RedisNotConfigured,

    RedisNotEntitled,

    RuntimeIncomplete,

    RedisAttestationUnavailable,

    RedisPersistenceDisabled,

    RedisWritePolicyUnsafe,

    RedisEvictionPolicyUnsafe,

    RedisUnreachable,
}

// Redis must be present and working; the startup durability attestation is published as information and never withholds a capability.
public static class RedisDurabilityStatuses
{
    public const string Attested = "attested";

    public const string Unverified = "unverified";

    public const string NotDurable = "not-durable";

    public static string? For(bool attested, DurableJobSubstrateCause? cause)
        => attested
            ? Attested
            : cause switch
            {
                DurableJobSubstrateCause.RedisAttestationUnavailable => Unverified,
                DurableJobSubstrateCause.RedisPersistenceDisabled
                    or DurableJobSubstrateCause.RedisWritePolicyUnsafe
                    or DurableJobSubstrateCause.RedisEvictionPolicyUnsafe => NotDurable,
                _ => null,
            };
}

public sealed record RedisDurabilityAttestation(
    string Endpoint,
    string PersistenceMode,
    string AcknowledgedWritePolicy,
    string EvictionPolicy,
    DateTimeOffset ObservedAt);

public sealed class DurableJobSubstrateOptions
{
    public const string SectionName = "DurableJobSubstrate";

    public bool RedisConfigured { get; set; }

    public bool RedisEntitled { get; set; }

    // The attestation is published and is never used to withhold a capability.
    public RedisDurabilityAttestation? RedisDurabilityAttestation { get; set; }

    public DurableJobSubstrateCause? RedisDurabilityFailure { get; set; }

    // The attestation detail is logged at startup and is never published on the anonymous capability manifest, because the message can name internal endpoints.
    public string? RedisDurabilityDetail { get; set; }

    public string? RedisDurabilityStatus
        => RedisDurabilityStatuses.For(RedisDurabilityAttestation is not null, RedisDurabilityFailure);

    // The job store and queue live in the system database, so a host that composed both runs jobs
    // whatever its Redis state. Without them the Redis facts still classify the Redis-backed
    // control plane (proposals, workflow operations), which callers ask about with neither present.
    public DurableJobSubstrateCause Classify(bool jobStorePresent, bool jobQueuePresent)
    {
        if (jobStorePresent && jobQueuePresent)
        {
            return DurableJobSubstrateCause.Available;
        }

        if (!RedisConfigured)
        {
            return DurableJobSubstrateCause.RedisNotConfigured;
        }

        if (!RedisEntitled)
        {
            return DurableJobSubstrateCause.RedisNotEntitled;
        }

        return RedisDurabilityFailure == DurableJobSubstrateCause.RedisUnreachable
            ? DurableJobSubstrateCause.RedisUnreachable
            : DurableJobSubstrateCause.RuntimeIncomplete;
    }
}
