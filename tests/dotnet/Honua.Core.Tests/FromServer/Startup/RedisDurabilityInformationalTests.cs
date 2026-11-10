// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Startup;

// The startup durability attestation of the Redis that stores operation proposals is published as
// information (attested, unverified, not-durable). It is never the reason a capability is
// withheld: managed Redis (AWS ElastiCache, MemoryDB) blocks CONFIG and, for ElastiCache Redis 7,
// has no AOF at all. Only Jobs:RequireDurableStore=true turns a non-attested outcome into a
// startup refusal.
public sealed class RedisDurabilityInformationalTests
{
    private const string ElastiCacheConfigRefusal =
        "ERR unknown command 'CONFIG', with args beginning with: 'GET' 'appendonly'";

    public static TheoryData<string, DurableJobSubstrateCause?, string> AttestationOutcomes => new()
    {
        { "attested", null, RedisDurabilityStatuses.Attested },
        { "config-blocked", DurableJobSubstrateCause.RedisAttestationUnavailable, RedisDurabilityStatuses.Unverified },
        { "appendonly-no", DurableJobSubstrateCause.RedisPersistenceDisabled, RedisDurabilityStatuses.NotDurable },
        { "appendfsync-no", DurableJobSubstrateCause.RedisWritePolicyUnsafe, RedisDurabilityStatuses.NotDurable },
        { "evicting", DurableJobSubstrateCause.RedisEvictionPolicyUnsafe, RedisDurabilityStatuses.NotDurable },
    };

    [UnitTheory]
    [MemberData(nameof(AttestationOutcomes))]
    public void ConnectedEntitledRedis_AnyAttestationOutcome_IsPublishedAndIsNeverTheCauseOfAWithheldCapability(
        string scenario,
        DurableJobSubstrateCause? outcome,
        string expectedStatus)
    {
        var options = Connected(outcome);

        options.RedisDurabilityStatus.Should().Be(expectedStatus);
        options.Classify(jobStorePresent: false, jobQueuePresent: false)
            .Should().Be(DurableJobSubstrateCause.RuntimeIncomplete,
                $"a host that composed no job store is not told its Redis is the reason ({scenario})");
        options.Classify(jobStorePresent: true, jobQueuePresent: true)
            .Should().Be(DurableJobSubstrateCause.Available, scenario);
    }

    [UnitTheory]
    [MemberData(nameof(AttestationOutcomes))]
    public void DefaultPolicy_AnyAttestationOutcome_DoesNotRefuseStartup(
        string scenario,
        DurableJobSubstrateCause? outcome,
        string expectedStatus)
    {
        _ = expectedStatus;
        var act = () => DurableJobSubstrateStartupGate.EnsureSatisfied(
            Connected(outcome),
            requireDurableStore: false,
            detail: ElastiCacheConfigRefusal);

        act.Should().NotThrow($"the attestation is information by default ({scenario})");
    }

    [UnitTest]
    public void ConfigBlockedRedis_IsUnverifiedNotARejection_AndItsGuidanceNeedsNoActionOnManagedRedis()
    {
        var options = Connected(DurableJobSubstrateCause.RedisAttestationUnavailable);

        options.RedisDurabilityStatus.Should().Be(RedisDurabilityStatuses.Unverified);
        DurableJobSubstrateRemediation.For(DurableJobSubstrateCause.RedisAttestationUnavailable)
            .Should().Contain("No action is required on managed Redis")
            .And.Contain("ElastiCache")
            .And.Contain("MemoryDB");
        DurableJobSubstrateRemediation.NonDurableConsequence
            .Should().Contain("stay enabled")
            .And.NotContain("will not advertise");
    }

    [UnitTest]
    public void RequireDurableStore_OnConfigBlockedRedis_RefusesWithTheUnverifiedCause()
    {
        var act = () => DurableJobSubstrateStartupGate.EnsureSatisfied(
            Connected(DurableJobSubstrateCause.RedisAttestationUnavailable),
            requireDurableStore: true,
            detail: ElastiCacheConfigRefusal);

        var thrown = act.Should().Throw<DurableJobSubstrateNotAttestedException>().Subject.Single();
        thrown.Cause.Should().Be(DurableJobSubstrateCause.RedisAttestationUnavailable);
        thrown.Detail.Should().Be(ElastiCacheConfigRefusal);
        thrown.Message.Should().Contain("Jobs:RequireDurableStore")
            .And.Contain("ElastiCache");
    }

    [UnitTest]
    public void RequireDurableStore_OnUnentitledRedis_StillReportsTheEntitlementFirst()
    {
        var options = Connected(DurableJobSubstrateCause.RedisAttestationUnavailable);
        options.RedisEntitled = false;

        var act = () => DurableJobSubstrateStartupGate.EnsureSatisfied(options, requireDurableStore: true);

        act.Should().Throw<DurableJobSubstrateNotAttestedException>()
            .Which.Cause.Should().Be(DurableJobSubstrateCause.RedisNotEntitled);
    }

    [UnitTest]
    public void UnreachableRedis_PublishesNoDurabilityOutcome_AndDoesNotWithholdTheJobRunner()
    {
        var options = Connected(DurableJobSubstrateCause.RedisUnreachable);

        options.RedisDurabilityStatus.Should().BeNull("a Redis that never connected was never inspected");
        options.Classify(jobStorePresent: false, jobQueuePresent: false)
            .Should().Be(DurableJobSubstrateCause.RedisUnreachable);
        options.Classify(jobStorePresent: true, jobQueuePresent: true)
            .Should().Be(DurableJobSubstrateCause.Available, "the job store and queue do not use Redis");
    }

    [UnitTest]
    public void UnentitledRedis_ReportsTheLicenceAheadOfTheAttestationOutcome()
    {
        var options = Connected(DurableJobSubstrateCause.RedisPersistenceDisabled);
        options.RedisEntitled = false;

        options.Classify(jobStorePresent: false, jobQueuePresent: false)
            .Should().Be(DurableJobSubstrateCause.RedisNotEntitled);
    }

    private static DurableJobSubstrateOptions Connected(DurableJobSubstrateCause? outcome) => new()
    {
        RedisConfigured = true,
        RedisEntitled = true,
        RedisDurabilityFailure = outcome,
        RedisDurabilityAttestation = outcome is null
            ? new RedisDurabilityAttestation(
                "redis:6379",
                "aof (appendonly=yes, aof_enabled=1)",
                "appendfsync=everysec",
                "noeviction",
                DateTimeOffset.UtcNow)
            : null,
    };
}
