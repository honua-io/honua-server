// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.FileStorage;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class GeoprocessingOutputStagingOptionsTests
{
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("1.00:00:01")]
    public void AddGeoprocessingOutputStaging_InvalidSweepInterval_FailsValidation(string sweepInterval)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{GeoprocessingOutputStagingOptions.SectionName}:SweepInterval"] = sweepInterval,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddGeoprocessingOutputStaging(configuration);
        using var provider = services.BuildServiceProvider();

        var readOptions = () => provider
            .GetRequiredService<IOptions<GeoprocessingOutputStagingOptions>>()
            .Value;

        readOptions.Should().Throw<OptionsValidationException>()
            .WithMessage("*SweepInterval*");
    }

    [UnitTest]
    public void AddGeoprocessingOutputStaging_DefaultSweepInterval_PassesValidation()
    {
        var services = new ServiceCollection();
        services.AddGeoprocessingOutputStaging(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptions<GeoprocessingOutputStagingOptions>>()
            .Value;

        options.SweepInterval.Should().Be(TimeSpan.FromMinutes(15));
    }
}
