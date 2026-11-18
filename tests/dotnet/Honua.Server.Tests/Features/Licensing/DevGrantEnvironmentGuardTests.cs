// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Features.Licensing;

public sealed class DevGrantEnvironmentGuardTests
{
    [UnitTheory]
    [InlineData("Staging")]
    [InlineData("QA")]
    [InlineData("production-east")]
    [InlineData("Prod")]
    public void DevGrant_OutsideDevelopmentAndTest_RefusesStartup(string environmentName)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Register(environmentName));
        Assert.Contains("DevGrantEdition is a test/dev-only", error.Message, StringComparison.Ordinal);
    }

    [UnitTheory]
    [InlineData("Development")]
    [InlineData("Test")]
    [InlineData("test")]
    public void DevGrant_InDevelopmentOrTest_IsAccepted(string environmentName)
        => Register(environmentName);

    private static void Register(string environmentName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Licensing:Mode"] = "Disabled",
            ["Licensing:DevGrantEdition"] = "Enterprise",
        }).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        new ServiceCollection().AddHonuaLicensing(configuration, environment);
    }
}
