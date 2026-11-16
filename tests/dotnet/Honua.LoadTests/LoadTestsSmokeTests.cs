// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.LoadTests.Scenarios;
using Honua.TestKit.Attributes;
using Honua.TestKit.Performance;
using NBomber.Contracts;
using Xunit;

namespace Honua.LoadTests;

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

    private const int LockedConcurrentVirtualUsers = 170;

    private const int LockedMinimumSteadyStateSeconds = 3600;
}
