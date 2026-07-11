// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Geoprocessing;
using Honua.Infrastructure.Hosting;
using Honua.Postgres;
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

        services.AddPostgreSqlServices(configuration);
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
}
