// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.FileStorage;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

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

    [UnitTest]
    public void AddGeoprocessing_StagingWithRedis_RegistersJobStoreAndSweeper()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{GeoprocessingOutputStagingOptions.SectionName}:Enabled"] = "true",
                [$"{GeoprocessingOutputStagingOptions.SectionName}:LocalRootPath"] = "staged-outputs",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());

        services.AddGeoprocessing(configuration);

        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IExecutionJobStore));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(GeoprocessingOutputArtifactSweeper));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IScheduledTickHandler)
            && descriptor.ImplementationType == typeof(GeoprocessingOutputArtifactSweeperScheduledTickHandler));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IHostedService));
    }
}
