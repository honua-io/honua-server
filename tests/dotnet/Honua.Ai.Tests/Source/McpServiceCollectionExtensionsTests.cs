// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Studio;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Hosting;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpServiceCollectionExtensionsTests
{
    [UnitTest]
    public void AddServerFeatures_DefaultComposition_WithoutCanonicalStores_DoesNotAdvertisePromotionResources()
    {
        var services = BuildBaseServices();
        services.AddServerFeatures(new ConfigurationBuilder().Build());

        RegisteredResourceHandlers(services)
            .Should().NotContain(new[]
            {
                typeof(PublishedServiceResource),
                typeof(DeploymentResource),
                typeof(MapPackageResource),
                typeof(AppPackageResource),
                typeof(PromotionSurfaceIndexResource)
            }, "the default server composition cannot advertise promotion URIs backed only by empty state");

        services.Any(d => d.ServiceType == typeof(IPublishedServiceStore))
            .Should().BeFalse("no canonical IPublishedServiceStore persistence is wired into AddServerFeatures yet");
        services.Any(d => d.ServiceType == typeof(IDeploymentStore))
            .Should().BeFalse("no canonical IDeploymentStore persistence is wired into AddServerFeatures yet");
    }

    [UnitTest]
    public void AddServerFeatures_WithCanonicalStores_AdvertisesPromotionResourcesByDefault()
    {
        var services = BuildBaseServices();
        services.AddSingleton(Substitute.For<IPublishedServiceStore>());
        services.AddSingleton(Substitute.For<IDeploymentStore>());

        services.AddServerFeatures(new ConfigurationBuilder().Build());

        RegisteredResourceHandlers(services)
            .Should().Contain(new[]
            {
                typeof(PublishedServiceResource),
                typeof(DeploymentResource),
                typeof(MapPackageResource),
                typeof(AppPackageResource),
                typeof(PromotionSurfaceIndexResource)
            }, "the default composition opts the promotion surface in once canonical publishing + deployment persistence is present (#1951)");
    }

}
