// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Features.Licensing;

// The bootstrap licence snapshot gates paid deployments before registration; it must honour a
// dev grant in exactly the environments the startup guard allows (Development and Test).
public sealed class DevGrantBootstrapSnapshotTests
{
    [UnitTheory]
    [InlineData("Staging")]
    [InlineData("Prod")]
    [InlineData("Production")]
    public async Task BootstrapSnapshot_OutsideDevelopmentAndTest_IgnoresDevGrant(string environmentName)
    {
        var snapshot = await LoadAsync(environmentName);

        Assert.NotEqual(HonuaEdition.Enterprise, snapshot.Edition);
    }

    [UnitTheory]
    [InlineData("Development")]
    [InlineData("Test")]
    public async Task BootstrapSnapshot_InDevelopmentOrTest_HonoursDevGrant(string environmentName)
    {
        var snapshot = await LoadAsync(environmentName);

        Assert.Equal(HonuaEdition.Enterprise, snapshot.Edition);
    }

    private static Task<LicenseSnapshot> LoadAsync(string environmentName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Licensing:Mode"] = "Enabled",
            ["Licensing:DevGrantEdition"] = "Enterprise",
        }).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        return StartupConfigurationHelpers.LoadBootstrapLicenseSnapshotAsync(configuration, environment);
    }
}
