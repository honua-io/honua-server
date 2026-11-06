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

    public string? RedisDurabilityDetail { get; set; }

    public string? RedisDurabilityStatus
        => RedisDurabilityStatuses.For(RedisDurabilityAttestation is not null, RedisDurabilityFailure);

    public DurableJobSubstrateCause Classify(bool jobStorePresent, bool jobQueuePresent)
    {
        // Entitlement is checked BEFORE durability, and fails closed (honua-server#4502). The
        // durability inspection runs against whatever infrastructure Redis is connected, which in
        // non-Development/Test deployments happens even when Redis is unentitled — so an
        // unentitled deployment whose Redis DOES attest would otherwise report Available and
        // advertise 'jobs.runner'. A present store never upgrades a missing entitlement.
        if (!RedisEntitled && RedisConfigured)
        {
            return DurableJobSubstrateCause.RedisNotEntitled;
        }

        // Owner ruling (2026-10-10): a CONNECTED Redis enables the durable job runner and the
        // governed proposal control plane whatever the durability attestation said. The
        // attestation outcome is published (RedisDurabilityStatus) and logged, never classified
        // here; only a Redis that is absent or did not connect withholds the capability.
        if (jobStorePresent && jobQueuePresent)
        {
            if (!RedisConfigured)
            {
                return DurableJobSubstrateCause.RedisNotConfigured;
            }

            return RedisDurabilityFailure == DurableJobSubstrateCause.RedisUnreachable
                ? DurableJobSubstrateCause.RedisUnreachable
                : DurableJobSubstrateCause.Available;
        }

        if (!RedisConfigured)
        {
            return DurableJobSubstrateCause.RedisNotConfigured;
        }

        return RedisEntitled
            ? RedisDurabilityFailure == DurableJobSubstrateCause.RedisUnreachable
                ? DurableJobSubstrateCause.RedisUnreachable
                : DurableJobSubstrateCause.RuntimeIncomplete
            : DurableJobSubstrateCause.RedisNotEntitled;
    }
}
