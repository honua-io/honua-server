// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.LoadTests.Scenarios;
using NBomber.Contracts;
using Xunit;

namespace Honua.LoadTests;

/// <summary>
/// PR-tier smoke tests that assert the load scenario classes compile, can be
/// instantiated, and produce valid <see cref="ScenarioProps"/>. These run in
/// every PR build so the project remains non-empty; the actual high-throughput
/// runs are gated behind <c>Tier=Slow</c> and only execute in the nightly
/// load-soak workflow.
/// </summary>
public sealed class LoadTestsSmokeTests
{
    [Fact]
    public void StacSearchScenario_IsDiscoverableAndConfigured()
    {
        var props = StacSearchLoadScenario.Build();

        Assert.NotNull(props);
        Assert.Equal(StacSearchLoadScenario.ScenarioName, props.ScenarioName);
        Assert.NotEmpty(props.LoadSimulations);
    }

    [Fact]
    public void TilesScenario_IsDiscoverableAndConfigured()
    {
        var props = TilesLoadScenario.Build();

        Assert.NotNull(props);
        Assert.Equal(TilesLoadScenario.ScenarioName, props.ScenarioName);
        Assert.NotEmpty(props.LoadSimulations);
        Assert.NotEmpty(TilesLoadScenario.TileCoordinates);
    }

    [Fact]
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

    [Fact]
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
}
