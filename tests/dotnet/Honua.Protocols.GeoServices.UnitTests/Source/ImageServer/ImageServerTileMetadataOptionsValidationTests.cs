// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerTileMetadataOptionsValidationTests
{
    [UnitTheory]
    [InlineData("-1")]
    [InlineData("29")]
    [InlineData("40")]
    public void StartupValidation_OutOfRangeMaxLevel_RefusesToStartNamingTheKey(string maxLevel)
    {
        using var provider = BuildProvider(maxLevel);

        var validate = () => provider.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().Throw<OptionsValidationException>()
            .WithMessage("*GeoServices:ImageServer:TileMetadata:MaxLevel must be between 0 and 28*");
    }

    [UnitTheory]
    [InlineData("0")]
    [InlineData("23")]
    [InlineData("28")]
    public void StartupValidation_MaxLevelInRange_Starts(string maxLevel)
    {
        using var provider = BuildProvider(maxLevel);

        provider.GetRequiredService<IStartupValidator>().Validate();

        provider.GetRequiredService<IOptions<ImageServerTileMetadataOptions>>().Value.MaxLevel
            .Should().Be(int.Parse(maxLevel, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static ServiceProvider BuildProvider(string maxLevel)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ImageServerTileMetadataOptions.SectionName}:Enabled"] = "true",
                [$"{ImageServerTileMetadataOptions.SectionName}:MaxLevel"] = maxLevel,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddImageServer(configuration);
        return services.BuildServiceProvider();
    }
}
