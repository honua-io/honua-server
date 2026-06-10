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
}
