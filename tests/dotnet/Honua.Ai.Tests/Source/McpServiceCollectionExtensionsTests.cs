// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Pins the DI shape for the MCP operator surface and the opt-in promotion
/// surface. The hosted-promotion resources (<c>honua://published-services*</c>,
/// <c>honua://deployments*</c>, <c>honua://map-packages*</c>,
/// <c>honua://app-packages*</c>) are intentionally gated behind
/// <see cref="McpServiceCollectionExtensions.AddMcpPromotionSurface"/> so the
/// default server composition cannot advertise promotion URIs backed only by
/// process-local empty state.
/// </summary>
public sealed partial class McpServiceCollectionExtensionsTests
{
    /// <summary>
    /// The default real-host composition
    /// (<see cref="FeatureRegistrationExtensions.AddServerFeatures"/>) gates the
    /// promotion surface on canonical publishing + deployment persistence
    /// (#1951). With no provider registering those stores, the gate stays closed
    /// and the promotion resources are NOT advertised — the same honesty
    /// invariant that keeps tools/list from advertising an always-empty surface.
    /// </summary>
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

    /// <summary>
    /// Positive branch of the #1951 gate: when canonical
    /// <see cref="IPublishedServiceStore"/> and <see cref="IDeploymentStore"/>
    /// persistence is registered before the default composition runs (as a
    /// durable provider does), <see cref="FeatureRegistrationExtensions.AddServerFeatures"/>
    /// opts the promotion surface in automatically, so a deployed server
    /// advertises honua://published-services|deployments|map-packages|app-packages.
    /// </summary>
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
