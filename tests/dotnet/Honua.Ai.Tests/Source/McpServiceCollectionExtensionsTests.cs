// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Hosting;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Resources;
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
    public void AddMcpOperatorSurface_RegistersFeatureCatalogResource()
    {
        var services = BuildBaseServices();
        services.AddMcpOperatorSurface(new ConfigurationBuilder().Build());

        RegisteredResourceHandlers(services)
            .Should().Contain(typeof(FeatureCatalogResource),
                "honua://catalog/features serves a static embedded drift-gated artifact (#1946), "
                + "so it is registered in the default composition unlike the persistence-backed "
                + "promotion resources.");
    }

    [UnitTest]
    public void AddMcpOperatorSurface_RegistersPublishServiceTool()
    {
        var services = BuildBaseServices();
        services.AddMcpOperatorSurface(new ConfigurationBuilder().Build());

        RegisteredToolHandlers(services)
            .Should().Contain(typeof(PublishServiceTool),
                "honua_publish_service is the default authoring/publishing tool (#1951); it gates "
                + "on IOperationInvoker at invocation time, so it is always advertised.");
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
    public void AddMcpOperatorSurface_WithLocationServicesRegistered_RegistersGeocodeAndRouteTools()
    {
        var services = BuildBaseServices();
        services.AddScoped(_ => Substitute.For<IGeocodeCoordinatorService>());
        services.AddScoped(_ => Substitute.For<IRoutingProvider>());

        services.AddMcpOperatorSurface(new ConfigurationBuilder().Build());

        RegisteredToolHandlers(services)
            .Should().Contain(new[]
            {
                typeof(GeocodeTool),
                typeof(RouteTool)
            }, "the default server composition registers geocoding and routing before the MCP operator surface");
    }

    [UnitTest]
    public void AddMcpOperatorSurface_WithoutOpsObservabilityReader_DoesNotRegisterOpsObservabilitySurface()
    {
        var services = BuildBaseServices();

        services.AddMcpOperatorSurface(new ConfigurationBuilder().Build());

        RegisteredToolHandlers(services)
            .Should().NotContain(new[]
            {
                typeof(OpsHealthTool),
                typeof(OpsFindingsTool),
                typeof(AlertEventsTool),
                typeof(OperateEventsTool)
            }, "ops observability tools must only be advertised when the server reader is wired");
        RegisteredResourceHandlers(services)
            .Should().NotContain(new[]
            {
                typeof(OpsHealthResource),
                typeof(OpsFindingsResource)
            }, "ops resources must only be advertised when the server reader is wired");
    }

    [UnitTest]
    public void AddMcpOperatorSurface_WithOpsObservabilityReader_RegistersOpsObservabilitySurface()
    {
        var services = BuildBaseServices();
        services.AddScoped(_ => Substitute.For<IMcpOpsObservabilityReader>());

        services.AddMcpOperatorSurface(new ConfigurationBuilder().Build());

        RegisteredToolHandlers(services)
            .Should().Contain(new[]
            {
                typeof(OpsHealthTool),
                typeof(OpsFindingsTool),
                typeof(AlertEventsTool),
                typeof(OperateEventsTool)
            }, "the full server composition registers the MCP ops reader before the operator surface");
        RegisteredResourceHandlers(services)
            .Should().Contain(new[]
            {
                typeof(OpsHealthResource),
                typeof(OpsFindingsResource)
            }, "ops health/findings are fixed MCP resources backed by the same reader");
    }

    [UnitTest]
    public void AddMcpOperatorSurface_WithoutPlatformOpsReader_DoesNotRegisterPlatformOpsTools()
    {
        var services = BuildBaseServices();

        services.AddMcpOperatorSurface(new ConfigurationBuilder().Build());

        RegisteredToolHandlers(services)
            .Should().NotContain(new[]
            {
                typeof(PlatformReleaseStatusTool),
                typeof(DeployOperationsTool),
                typeof(ProposeRollbackTool)
            }, "platform-ops tools must only be advertised when the server reader is wired");
    }

    [UnitTest]
    public void AddMcpOperatorSurface_WithPlatformOpsReader_RegistersPlatformOpsTools()
    {
        var services = BuildBaseServices();
        services.AddScoped(_ => Substitute.For<IMcpPlatformOpsReader>());

        services.AddMcpOperatorSurface(new ConfigurationBuilder().Build());

        RegisteredToolHandlers(services)
            .Should().Contain(new[]
            {
                typeof(PlatformReleaseStatusTool),
                typeof(DeployOperationsTool),
                typeof(ProposeRollbackTool)
            }, "the full server composition registers the MCP platform-ops reader before the operator surface");
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

    private static IEnumerable<Type?> RegisteredToolHandlers(IServiceCollection services)
        => services
            .Where(d => d.ServiceType == typeof(IMcpTool))
            .Select(d => d.ImplementationType);

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
