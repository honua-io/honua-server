// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Geoprocessing;
using Honua.Infrastructure.Hosting;
using Honua.Db.Postgres;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Startup;

/// <summary>
/// Verifies that the real Postgres-first server composition advertises the
/// durable MCP promotion resources (#2482).
/// </summary>
public sealed class PostgresPromotionSurfaceRegistrationTests
{
    [UnitTest]
    public void PostgresThenServerFeatures_AdvertisesPromotionResources()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=honua;Username=honua;Password=honua"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IGeoprocessingJobService>());

        services.AddPostgreSqlServices(configuration, ServerCoreSchemaMigrations.Manifest);
        services.AddServerFeatures(configuration);

        var resourceTypes = services
            .Where(descriptor => descriptor.ServiceType == typeof(IMcpResource))
            .Select(descriptor => descriptor.ImplementationType)
            .ToArray();
        resourceTypes.Should().Contain(new[]
        {
            typeof(PublishedServiceResource),
            typeof(DeploymentResource),
            typeof(MapPackageResource),
            typeof(AppPackageResource),
            typeof(PromotionSurfaceIndexResource)
        });
    }

    [UnitTest]
    public void GraphProviderWithoutPromotionStores_AdvertisesOnlyCatalogPublicationResources()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IGeoprocessingJobService>());
        services.AddSingleton(Substitute.For<Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphProvider>());

        services.AddServerFeatures(configuration);

        services.Any(descriptor => descriptor.ServiceType ==
            typeof(Honua.Core.Features.Publishing.Abstractions.IPublishedServiceStore)).Should().BeFalse();
        services.Any(descriptor => descriptor.ServiceType ==
            typeof(Honua.Core.Features.Deployment.Abstractions.IDeploymentStore)).Should().BeFalse();
        var catalogFactories = services.Where(descriptor =>
            descriptor.ServiceType == typeof(IMcpResource) && descriptor.ImplementationFactory is not null).ToArray();
        catalogFactories.Should().HaveCount(2);
        using var provider = services.BuildServiceProvider();
        var resources = catalogFactories
            .Select(descriptor => (IMcpResource)descriptor.ImplementationFactory!(provider)!)
            .ToArray();
        resources.Select(resource => resource.GetType()).Should().BeEquivalentTo(
            new[] { typeof(PublishedServiceResource), typeof(PromotionSurfaceIndexResource) });
        resources.SelectMany(resource => resource.Describe()).Select(descriptor => descriptor.Uri)
            .Should().Equal("honua://published-services");
        resources.Any(resource => resource.CanHandle("honua://deployments")).Should().BeFalse();
        resources.Any(resource => resource.CanHandle("honua://map-packages")).Should().BeFalse();
        resources.Any(resource => resource.CanHandle("honua://app-packages")).Should().BeFalse();
    }
}
