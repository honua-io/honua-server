// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Infrastructure.Hosting;
using Honua.Server.Features.Protocols.Mcp;
using Honua.Server.Features.Protocols.Mcp.Resources;
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
public sealed class McpServiceCollectionExtensionsTests
{
    [UnitTest]
    public void AddMcpOperatorSurface_DoesNotRegisterPromotionResourceHandlers()
    {
        var services = BuildBaseServices();
        services.AddMcpOperatorSurface(new ConfigurationBuilder().Build());

        RegisteredResourceHandlers(services)
            .Should().NotContain(new[]
            {
                typeof(PublishedServiceResource),
                typeof(DeploymentResource),
                typeof(MapPackageResource),
                typeof(AppPackageResource),
                typeof(PromotionSurfaceIndexResource)
            }, "the promotion surface is opt-in via AddMcpPromotionSurface");
    }

    [UnitTest]
    public void AddMcpOperatorSurface_DoesNotRegisterFallbackPromotionStores()
    {
        var services = BuildBaseServices();
        services.AddMcpOperatorSurface(new ConfigurationBuilder().Build());

        services.Any(d => d.ServiceType == typeof(IPublishedServiceStore))
            .Should().BeFalse("canonical publishing persistence is not yet registered by the default composition");
        services.Any(d => d.ServiceType == typeof(IDeploymentStore))
            .Should().BeFalse("canonical deployment persistence is not yet registered by the default composition");
        services.Any(d => d.ServiceType == typeof(IPublishIntentStore))
            .Should().BeFalse("canonical publish-intent persistence is not yet registered by the default composition");
    }

    [UnitTest]
    public void AddMcpPromotionSurface_RegistersPromotionResourceHandlersOnly()
    {
        var services = BuildBaseServices();
        services.AddMcpOperatorSurface(new ConfigurationBuilder().Build());
        services.AddMcpPromotionSurface();

        RegisteredResourceHandlers(services)
            .Should().Contain(new[]
            {
                typeof(PublishedServiceResource),
                typeof(DeploymentResource),
                typeof(MapPackageResource),
                typeof(AppPackageResource),
                typeof(PromotionSurfaceIndexResource)
            });

        services.Any(d => d.ServiceType == typeof(IPublishedServiceStore))
            .Should().BeFalse("AddMcpPromotionSurface must not register any store fallbacks");
        services.Any(d => d.ServiceType == typeof(IDeploymentStore))
            .Should().BeFalse("AddMcpPromotionSurface must not register any store fallbacks");
        services.Any(d => d.ServiceType == typeof(IPublishIntentStore))
            .Should().BeFalse("AddMcpPromotionSurface must not register any store fallbacks");
    }

    [UnitTest]
    public void AddMcpPromotionSurface_WithCanonicalPersistenceAlreadyRegistered_ResolvesSurfaceFromCanonicalStores()
    {
        var canonicalPublishedServices = Substitute.For<IPublishedServiceStore>();
        var canonicalDeployments = Substitute.For<IDeploymentStore>();

        var services = BuildBaseServices();
        services.AddSingleton(canonicalPublishedServices);
        services.AddSingleton(canonicalDeployments);
        services.AddMcpOperatorSurface(new ConfigurationBuilder().Build());
        services.AddMcpPromotionSurface();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IPublishedServiceStore>().Should().BeSameAs(canonicalPublishedServices);
        provider.GetRequiredService<IDeploymentStore>().Should().BeSameAs(canonicalDeployments);
        provider.GetServices<IMcpResource>().OfType<PublishedServiceResource>().Should().ContainSingle();
        provider.GetServices<IMcpResource>().OfType<DeploymentResource>().Should().ContainSingle();
    }

    /// <summary>
    /// Tripwire for the default real-host composition
    /// (<see cref="FeatureRegistrationExtensions.AddServerFeatures"/>): the
    /// promotion surface must remain gated off until canonical publishing and
    /// deployment persistence register earlier in the composition root. When
    /// that lands, this assertion flips and forces a deliberate update to call
    /// <see cref="McpServiceCollectionExtensions.AddMcpPromotionSurface"/> in
    /// the default composition.
    /// </summary>
    [UnitTest]
    public void AddServerFeatures_DefaultComposition_DoesNotAdvertisePromotionResources()
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

    private static IEnumerable<Type?> RegisteredResourceHandlers(IServiceCollection services)
        => services
            .Where(d => d.ServiceType == typeof(IMcpResource))
            .Select(d => d.ImplementationType);

    private static ServiceCollection BuildBaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IGeoprocessingJobService>());
        return services;
    }
}
