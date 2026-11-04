// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Server.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Startup;

public sealed class RenamedConfigurationSectionsTests
{
    [UnitTheory]
    [InlineData("ArcGisRest:Enabled", "false")]
    [InlineData("ArcGisRest:TimeoutSeconds", "30")]
    public void RegisterInfrastructureServices_RetiredSectionPresent_RefusesToStartAndNamesTheNewSection(string key, string value)
    {
        var configuration = Configuration((key, value));

        var register = () => InfrastructureCompositionRoot.RegisterInfrastructureServices(new ServiceCollection(), configuration);

        register.Should().Throw<InvalidOperationException>()
            .WithMessage("*'ArcGisRest' configuration section was renamed to 'GeoServicesRest'*");
    }

    [UnitTest]
    public void ThrowIfRetiredSectionPresent_OnlyCurrentSection_Accepts()
    {
        var configuration = Configuration(("GeoServicesRest:Enabled", "false"));

        var check = () => RenamedConfigurationSections.ThrowIfRetiredSectionPresent(configuration);

        check.Should().NotThrow();
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => KeyValuePair.Create(setting.Key, (string?)setting.Value)))
            .Build();
}
