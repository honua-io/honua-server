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
public sealed class McpServiceCollectionExtensionsTests
{
    [UnitTest]
    public void AddMcpDataAccessSurface_DoesNotRegisterPromotionResourceHandlers()
    {
        var services = BuildBaseServices();
        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());

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
    public void AddMcpDataAccessSurface_RegistersFeatureCatalogResource()
    {
        var services = BuildBaseServices();
        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());

        RegisteredResourceHandlers(services)
            .Should().Contain(typeof(FeatureCatalogResource),
                "honua://catalog/features serves a static embedded drift-gated artifact (#1946), "
                + "so it is registered in the default composition unlike the persistence-backed "
                + "promotion resources.");
    }

    [UnitTest]
    public void AddMcpDataAccessSurface_RegistersPublishServiceTool()
    {
        var services = BuildBaseServices();
        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());

        RegisteredToolHandlers(services)
            .Should().Contain(typeof(PublishServiceTool),
                "honua_publish_service is the default authoring/publishing tool (#1951); it gates "
                + "on IOperationInvoker at invocation time, so it is always advertised.");
    }

    /// <summary>
    /// Regression test for PR #3016 review (P1): in the real server composition
    /// root (<c>FeatureRegistrationExtensions.AddServerFeatures</c>),
    /// <c>AddMcpDataAccessSurface</c> runs BEFORE <c>AddStudioPackageLifecycle</c>.
    /// A descriptor-presence gate at registration time (<c>services.Any(d =&gt;
    /// d.ServiceType == typeof(IStudioPackageLifecycleService))</c>) would never
    /// see the lifecycle service in that order and silently drop the twelve
    /// Studio tools from <c>tools/list</c>. The tools must be registered
    /// unconditionally and resolve <c>IStudioPackageLifecycleService</c> per
    /// request instead, so registration order never matters.
    /// </summary>
    [UnitTest]
    public void AddMcpDataAccessSurface_RegistersStudioTools_EvenWhenLifecycleServiceIsRegisteredAfter()
    {
        var services = BuildBaseServices();
        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());
        // Mirrors the real (buggy-if-gated) order: lifecycle registration AFTER
        // the MCP surface, exactly as FeatureRegistrationExtensions.cs has it.
        services.AddStudioPackageLifecycle();

        RegisteredToolHandlers(services).Should().Contain(new[]
        {
            typeof(CreateStudioDraftTool),
            typeof(GetStudioDraftTool),
            typeof(UpdateStudioDraftTool),
            typeof(ValidateStudioDraftTool),
            typeof(PreviewStudioDraftTool),
            typeof(AddStudioLayerTool),
            typeof(RemoveStudioLayerTool),
            typeof(SetStudioLayerStyleTool),
            typeof(SetStudioViewTool),
            typeof(AddStudioWidgetTool),
            typeof(RemoveStudioWidgetTool),
            typeof(ProposeStudioPublicationTool),
        }, "Studio tools must be advertised regardless of AddStudioPackageLifecycle registration order, "
            + "because they resolve the scoped lifecycle service per-request rather than via constructor injection");
    }

    /// <summary>
    /// Companion to the order-independence test above: the tools must also be
    /// registered when the lifecycle service is composed FIRST (today's actual
    /// order is irrelevant to correctness either way).
    /// </summary>
    [UnitTest]
    public void AddMcpDataAccessSurface_RegistersStudioTools_WhenLifecycleServiceIsRegisteredFirst()
    {
        var services = BuildBaseServices();
        services.AddStudioPackageLifecycle();
        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());

        RegisteredToolHandlers(services).Should().Contain(typeof(CreateStudioDraftTool));
    }

    /// <summary>
    /// Regression test for PR #3016 review (P2): the Studio tool descriptors
    /// are registered Singleton (like every other <c>/mcp</c> tool), while
    /// <c>AddStudioPackageLifecycle</c> registers <c>IStudioPackageLifecycleService</c>
    /// and <c>IStudioPackageValidator</c> Scoped. Activating each Studio tool
    /// type from the ROOT provider (exactly what constructing the singleton
    /// catalog at host startup does) must not throw — which it would if a
    /// tool's constructor still required a Scoped service instead of
    /// resolving it per-request from <c>httpContext.RequestServices</c>.
    /// Scoped to <c>AddStudioPackageLifecycle</c> + the tools' own
    /// dependencies (not the full <c>AddMcpDataAccessSurface</c> graph, which
    /// pulls in unrelated collaborators — grounding, package review, etc. —
    /// this test has no interest in constructing).
    /// </summary>
    [UnitTest]
    public void StudioToolSingletons_ActivateFromTheRootProvider_WithoutCapturingScopedServices()
    {
        var services = BuildBaseServices();
        services.AddStudioPackageLifecycle();
        using var provider = services.BuildServiceProvider();

        Type[] studioToolTypes =
        [
            typeof(CreateStudioDraftTool),
            typeof(GetStudioDraftTool),
            typeof(UpdateStudioDraftTool),
            typeof(ValidateStudioDraftTool),
            typeof(PreviewStudioDraftTool),
            typeof(AddStudioLayerTool),
            typeof(RemoveStudioLayerTool),
            typeof(SetStudioLayerStyleTool),
            typeof(SetStudioViewTool),
            typeof(AddStudioWidgetTool),
            typeof(RemoveStudioWidgetTool),
            typeof(ProposeStudioPublicationTool),
        ];

        foreach (var toolType in studioToolTypes)
        {
            var act = () => ActivatorUtilities.CreateInstance(provider, toolType);
            act.Should().NotThrow(
                $"'{toolType.Name}' must be constructible from the root provider — its constructor must not "
                + "require IStudioPackageLifecycleService/IStudioPackageValidator (both Scoped)");
        }
    }

    [UnitTest]
    public void AddMcpDataAccessSurface_DoesNotRegisterFallbackPromotionStores()
    {
        var services = BuildBaseServices();
        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());

        services.Any(d => d.ServiceType == typeof(IPublishedServiceStore))
            .Should().BeFalse("the transport registrar must not invent canonical publishing persistence");
        services.Any(d => d.ServiceType == typeof(IDeploymentStore))
            .Should().BeFalse("the transport registrar must not invent canonical deployment persistence");
        services.Any(d => d.ServiceType == typeof(IPublishIntentStore))
            .Should().BeFalse("canonical publish-intent persistence is not yet registered by the default composition");
    }

    [UnitTest]
    public void AddMcpDataAccessSurface_WithLocationServicesRegistered_RegistersGeocodeAndRouteTools()
    {
        var services = BuildBaseServices();
        services.AddScoped(_ => Substitute.For<IGeocodeCoordinatorService>());
        services.AddScoped(_ => Substitute.For<IRoutingProvider>());

        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());

        RegisteredToolHandlers(services)
            .Should().Contain(new[]
            {
                typeof(GeocodeTool),
                typeof(RouteTool)
            }, "the default server composition registers geocoding and routing before the MCP operator surface");
    }

    [UnitTest]
    public void AddMcpDataAccessSurface_WithoutOpsObservabilityReader_DoesNotRegisterOpsObservabilitySurface()
    {
        var services = BuildBaseServices();

        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());

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
    public void AddMcpDataAccessSurface_WithOpsObservabilityReader_RegistersOpsObservabilitySurface()
    {
        var services = BuildBaseServices();
        services.AddScoped(_ => Substitute.For<IMcpOpsObservabilityReader>());

        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());

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
    public void AddMcpDataAccessSurface_WithoutPlatformOpsReader_DoesNotRegisterPlatformOpsTools()
    {
        var services = BuildBaseServices();

        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());

        RegisteredToolHandlers(services)
            .Should().NotContain(new[]
            {
                typeof(PlatformReleaseStatusTool),
                typeof(DeployOperationsTool),
                typeof(SupportedOperationKindsTool),
                typeof(ProposeRollbackTool)
            }, "platform-ops tools must only be advertised when the server reader is wired");
    }

    [UnitTest]
    public void AddMcpDataAccessSurface_WithPlatformOpsReader_RegistersPlatformOpsTools()
    {
        var services = BuildBaseServices();
        services.AddScoped(_ => Substitute.For<IMcpPlatformOpsReader>());

        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());

        RegisteredToolHandlers(services)
            .Should().Contain(new[]
            {
                typeof(PlatformReleaseStatusTool),
                typeof(DeployOperationsTool),
                typeof(SupportedOperationKindsTool),
                typeof(ProposeRollbackTool)
            }, "the full server composition registers the MCP platform-ops reader before the operator surface");
    }

    [UnitTest]
    public void AddMcpPromotionSurface_RegistersPromotionResourceHandlersOnly()
    {
        var services = BuildBaseServices();
        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());
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
        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());
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
