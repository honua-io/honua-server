// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.LoadTests.Scenarios;
using Honua.TestKit.Attributes;
using Honua.TestKit.Performance;
using NBomber.Contracts;
using Xunit;

namespace Honua.LoadTests;

/// <summary>
/// PR-tier smoke tests for the real NBomber load scenarios that ship under
/// <c>./Scenarios/</c> (<see cref="StacSearchLoadScenario"/>,
/// <see cref="TilesLoadScenario"/>, <see cref="FeaturesPaginationLoadScenario"/>,
/// and <see cref="LoadScenarioSettings"/>). Each fact instantiates a scenario
/// and asserts it produces a valid, configured <see cref="ScenarioProps"/> with
/// at least one load simulation, so a broken or removed scenario fails the PR
/// build instead of silently disappearing.
///
/// These are NOT the full load runs — the actual high-throughput executions
/// are gated behind <c>Tier=Slow</c> and only run in the nightly load-soak
/// workflow. The presence and shape of the scenarios themselves is what this
/// PR-tier suite guards.
/// </summary>
public sealed class LoadTestsSmokeTests
{
    [UnitTest]
    public void StacSearchScenario_IsDiscoverableAndConfigured()
    {
        var props = StacSearchLoadScenario.Build();

        Assert.NotNull(props);
        Assert.Equal(StacSearchLoadScenario.ScenarioName, props.ScenarioName);
        Assert.NotEmpty(props.LoadSimulations);
    }

    [UnitTest]
    public void TilesScenario_IsDiscoverableAndConfigured()
    {
        var props = TilesLoadScenario.Build();

        Assert.NotNull(props);
        Assert.Equal(TilesLoadScenario.ScenarioName, props.ScenarioName);
        Assert.NotEmpty(props.LoadSimulations);
        Assert.NotEmpty(TilesLoadScenario.TileCoordinates);
    }

    [UnitTest]
    public void FeaturesPaginationScenario_IsDiscoverableAndConfigured()
    {
        var props = FeaturesPaginationLoadScenario.Build();

        Assert.NotNull(props);
        Assert.Equal(FeaturesPaginationLoadScenario.ScenarioName, props.ScenarioName);
        Assert.NotEmpty(props.LoadSimulations);
        Assert.NotEmpty(FeaturesPaginationLoadScenario.OffsetWalk);
        // Offsets must walk 0..MaxOffset in `Limit`-sized steps.
        Assert.Equal(0, FeaturesPaginationLoadScenario.OffsetWalk[0]);
        Assert.Equal(
            FeaturesPaginationLoadScenario.MaxOffset,
            FeaturesPaginationLoadScenario.OffsetWalk[^1]);
    }

    [UnitTest]
    public void Settings_DefaultBaseUrl_WhenEnvVarUnset()
    {
        var originalTarget = Environment.GetEnvironmentVariable(LoadScenarioSettings.TargetEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(LoadScenarioSettings.TargetEnvVar, null);
            Assert.Equal(LoadScenarioSettings.DefaultBaseUrl, LoadScenarioSettings.GetBaseUrl());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LoadScenarioSettings.TargetEnvVar, originalTarget);
        }
    }

    [UnitTest]
    public void CliScenarioAllowlist_MatchesRegisteredLoadSuite()
    {
        Assert.Equal(
            LoadTestScenarios.ScenarioNames.OrderBy(static name => name, StringComparer.Ordinal),
            Program.KnownScenarios.OrderBy(static name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// The soak profile is the one the frozen 2026.1 capacity lock names
    /// (honua-release <c>certification/capacity-envelope.v1.json</c>: <c>soak.profile</c>,
    /// <c>soak.minimumSteadyStateSeconds</c>, <c>supportedEnvelope.concurrentVirtualUsers</c>).
    /// A published capacity receipt claims that envelope, so drifting these numbers would make the
    /// receipt claim concurrency the run never drove, or a steady state shorter than the lock's
    /// minimum. The lock is frozen and lives in another repository; this test is the local guard
    /// that the profile still matches it.
    /// </summary>
    [UnitTest]
    public void SoakProfile_MatchesFrozenCapacityEnvelope()
    {
        var soak = LoadTestProfile.Soak;

        Assert.Equal(LockedConcurrentVirtualUsers, soak.TotalVirtualUsers);
        Assert.True(
            soak.Duration >= TimeSpan.FromSeconds(LockedMinimumSteadyStateSeconds),
            $"soak steady state {soak.Duration} is below the locked minimum of {LockedMinimumSteadyStateSeconds}s");
        Assert.Equal(LoadTestProfile.Soak, LoadTestProfile.FromName("soak"));
    }

    /// <summary><c>supportedEnvelope.concurrentVirtualUsers</c> in the frozen 2026.1 lock.</summary>
    private const int LockedConcurrentVirtualUsers = 170;

    /// <summary><c>soak.minimumSteadyStateSeconds</c> in the frozen 2026.1 lock.</summary>
    private const int LockedMinimumSteadyStateSeconds = 3600;
}
