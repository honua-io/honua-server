// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Startup;

/// <summary>
/// Owner ruling (2026-10-10): the governed proposal control plane and the durable job runner
/// require Redis to be present and working, NOT a <c>CONFIG GET</c>-proven durable policy. The
/// startup durability attestation is published as information (<c>attested</c> /
/// <c>unverified</c> / <c>not-durable</c>) and never withholds <c>jobs.runner</c> or
/// <c>operations.proposals</c>; managed Redis (AWS ElastiCache, MemoryDB) blocks <c>CONFIG</c>
/// and, for ElastiCache Redis 7, has no AOF at all. Only a Redis that is absent, unentitled or
/// unreachable withholds the capabilities, and only <c>Jobs:RequireDurableStore=true</c> turns a
/// non-attested outcome into a startup refusal.
/// </summary>
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
    public void ConnectedEntitledRedis_AnyAttestationOutcome_KeepsTheRuntimeAvailableAndPublishesTheOutcome(
        string scenario,
        DurableJobSubstrateCause? outcome,
        string expectedStatus)
    {
        var options = Connected(outcome);

        options.Classify(jobStorePresent: true, jobQueuePresent: true)
            .Should().Be(DurableJobSubstrateCause.Available,
                $"a connected Redis enables jobs.runner and operations.proposals whatever the attestation said ({scenario})");
        options.RedisDurabilityStatus.Should().Be(expectedStatus);
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
    public void UnreachableRedis_WithholdsTheRuntime_AndPublishesNoDurabilityOutcome()
    {
        var options = Connected(DurableJobSubstrateCause.RedisUnreachable);

        options.Classify(jobStorePresent: true, jobQueuePresent: true)
            .Should().Be(DurableJobSubstrateCause.RedisUnreachable,
                "a Redis that never connected is genuinely unavailable, unlike an attestation outcome");
        options.Classify(jobStorePresent: false, jobQueuePresent: false)
            .Should().Be(DurableJobSubstrateCause.RedisUnreachable);
        options.RedisDurabilityStatus.Should().BeNull();
    }

    [UnitTest]
    public void RedisNotConfigured_StillWithholdsTheRuntime()
    {
        var options = new DurableJobSubstrateOptions { RedisConfigured = false, RedisEntitled = true };

        options.Classify(jobStorePresent: false, jobQueuePresent: false)
            .Should().Be(DurableJobSubstrateCause.RedisNotConfigured);
        options.Classify(jobStorePresent: true, jobQueuePresent: true)
            .Should().Be(DurableJobSubstrateCause.RedisNotConfigured,
                "only a configured Redis backs the advertised runtime");
        options.RedisDurabilityStatus.Should().BeNull();
    }

    [UnitTest]
    public void UnentitledRedis_StillReportsLicenseRequired_WhateverTheAttestation()
    {
        var options = Connected(DurableJobSubstrateCause.RedisPersistenceDisabled);
        options.RedisEntitled = false;

        options.Classify(jobStorePresent: true, jobQueuePresent: true)
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
