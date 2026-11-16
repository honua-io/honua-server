// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Drafts;
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
            typeof(SetStudioLayerVisibilityTool),
            typeof(SetStudioMapStyleTool),
            typeof(SetStudioViewTool),
            typeof(AddStudioWidgetTool),
            typeof(RemoveStudioWidgetTool),
            typeof(ProposeStudioPublicationTool),
        }, "Studio tools must be advertised regardless of AddStudioPackageLifecycle registration order, "
            + "because they resolve the scoped lifecycle service per-request rather than via constructor injection");
    }

    [UnitTest]
    public void AddMcpDataAccessSurface_RegistersStudioTools_WhenLifecycleServiceIsRegisteredFirst()
    {
        var services = BuildBaseServices();
        services.AddStudioPackageLifecycle();
        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());

        RegisteredToolHandlers(services).Should().Contain(typeof(CreateStudioDraftTool));
    }

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
            typeof(SetStudioLayerVisibilityTool),
            typeof(SetStudioMapStyleTool),
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

    [UnitTest]
    public void AddStudioDraftFactories_WithoutASystemDatabase_ResolvesInMemoryPackageDraftStore()
    {
        var services = BuildBaseServices();
        services.AddStudioDraftFactories();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IPackageDraftStore>().Should().BeOfType<InMemoryPackageDraftStore>(
            "a host with no system database keeps the single-process store");
    }

    [UnitTest]
    public void AddMcpDataAccessSurface_WithoutASystemDatabase_ResolvesInMemoryPackageDraftStore()
    {
        var services = BuildBaseServices();
        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IPackageDraftStore>().Should().BeOfType<InMemoryPackageDraftStore>();
    }

    [UnitTest]
    public async Task AddMcpDataAccessSurface_WithoutASystemDatabase_PublishesLocalNotifications()
    {
        var services = BuildBaseServices();
        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();
        var sessions = provider.GetRequiredService<McpSessionManager>();
        var sessionId = sessions.CreateSession();
        sessions.TryGetReader(sessionId, out var reader).Should().BeTrue();
        var publisher = provider.GetRequiredService<IMcpNotificationPublisher>();

        (await publisher.PublishProgress(sessionId, "local-job", 42, 100, "Running")).Should().BeTrue();
        reader.TryRead(out var progress).Should().BeTrue();
        progress.Should().Contain("notifications/progress");
        (await publisher.BroadcastResourcesListChanged()).Should().Be(1);
        reader.TryRead(out var broadcast).Should().BeTrue();
        broadcast.Should().Contain("notifications/resources/list_changed");
        (await publisher.PublishProgress("unknown-session", "local-job", 42, 100, null)).Should().BeFalse();
    }

    [UnitTest]
    public void AddMcpDataAccessSurface_WithCallerSuppliedStore_KeepsThatStore()
    {
        var custom = Substitute.For<IPackageDraftStore>();
        var services = BuildBaseServices();
        services.AddSingleton(custom);
        services.AddMcpDataAccessSurface(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IPackageDraftStore>().Should().BeSameAs(custom);
    }

}
