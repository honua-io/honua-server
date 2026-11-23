// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed class OutputCacheTtlConfigurationTests
{
    [UnitTheory]
    [Operation(Operations.Cache)]
    [InlineData("MvtTile", "00:00:00")]
    [InlineData("TileJson", "-00:05:00")]
    public void ConfigureOutputCaching_RefusesNonPositiveLifetime_AtStartup(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"OutputCache:{key}"] = value })
            .Build();

        var configure = () => OutputCachingRegistration.ConfigureOutputCaching(new ServiceCollection(), configuration);

        configure.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain($"OutputCache:{key}");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void ConfigureOutputCaching_AcceptsProductDefaults()
    {
        var configure = () => OutputCachingRegistration.ConfigureOutputCaching(
            new ServiceCollection(),
            new ConfigurationBuilder().Build());

        configure.Should().NotThrow();
    }

    // Scene endpoints write their Cache-Control from these options, so they must see the operator's values too.
    [UnitTest]
    [Operation(Operations.Cache)]
    public void ConfigureOutputCaching_ExposesConfiguredLifetimesToEndpoints()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OutputCache:SceneTileAsset"] = "00:02:00" })
            .Build();
        var services = new ServiceCollection();

        OutputCachingRegistration.ConfigureOutputCaching(services, configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<OutputCacheTtlOptions>>().Value.SceneTileAsset
            .Should().Be(TimeSpan.FromMinutes(2));
    }
}
