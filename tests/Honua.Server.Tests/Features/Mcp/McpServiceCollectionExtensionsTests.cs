// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Infrastructure.Hosting;
using Honua.Server.Features.Mcp;
using Honua.Server.Features.Mcp.Stores;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Mcp;

/// <summary>
/// Pins the promotion-surface DI wiring to the fallback-backed pattern
/// documented in <c>docs/developer/MCP_SERVER.md</c>. The promotion resources
/// are functional handlers (dispatcher tags reads as <c>status=ok</c>), and
/// <c>AddMcpOperatorSurface</c> registers the in-memory stores via
/// <c>TryAddSingleton</c> so DI always resolves. When canonical
/// publishing/deployment persistence later registers earlier in the
/// composition root, the fallback registrations become no-ops and the same
/// handlers immediately surface real lifecycle data without an API change.
/// </summary>
public sealed class McpServiceCollectionExtensionsTests
{
    [UnitTest]
    public void AddMcpOperatorSurface_WithoutCanonicalPersistence_ResolvesInMemoryPromotionStoresFallback()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IPublishedServiceStore>()
            .Should().BeOfType<InMemoryPublishedServiceStore>();
        provider.GetRequiredService<IPublishIntentStore>()
            .Should().BeOfType<InMemoryPublishIntentStore>();
        provider.GetRequiredService<IDeploymentStore>()
            .Should().BeOfType<InMemoryDeploymentStore>();
    }

    [UnitTest]
    public void AddMcpOperatorSurface_WithCanonicalPersistenceAlreadyRegistered_LeavesCanonicalStoresInPlace()
    {
        var canonicalPublishedServices = Substitute.For<IPublishedServiceStore>();
        var canonicalIntents = Substitute.For<IPublishIntentStore>();
        var canonicalDeployments = Substitute.For<IDeploymentStore>();

        var services = BuildBaseServices();
        services.AddSingleton(canonicalPublishedServices);
        services.AddSingleton(canonicalIntents);
        services.AddSingleton(canonicalDeployments);
        services.AddMcpOperatorSurface(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IPublishedServiceStore>().Should().BeSameAs(canonicalPublishedServices);
        provider.GetRequiredService<IPublishIntentStore>().Should().BeSameAs(canonicalIntents);
        provider.GetRequiredService<IDeploymentStore>().Should().BeSameAs(canonicalDeployments);
    }

    /// <summary>
    /// Tripwire for the default real-host composition (<see cref="FeatureRegistrationExtensions.AddServerFeatures"/>).
    /// Nothing in the default server composition registers canonical publishing/deployment
    /// persistence today, so <see cref="IPublishedServiceStore"/>, <see cref="IPublishIntentStore"/>,
    /// and <see cref="IDeploymentStore"/> still resolve to the in-memory fallbacks registered
    /// by <see cref="McpServiceCollectionExtensions.AddMcpOperatorSurface"/>. This test pins that
    /// known gap — documented in <c>docs/developer/MCP_SERVER.md</c> — so that when a downstream
    /// ticket wires canonical persistence earlier in the composition root, the assertion flips
    /// and forces a deliberate update (either delete the fallback registrations or narrow this
    /// test to the new wiring).
    /// </summary>
    [UnitTest]
    public void AddServerFeatures_DefaultComposition_StillResolvesInMemoryPromotionStoresFallback()
    {
        var services = BuildBaseServices();
        services.AddServerFeatures(new ConfigurationBuilder().Build());

        ResolveImplementationType(services, typeof(IPublishedServiceStore))
            .Should().Be<InMemoryPublishedServiceStore>(
                "canonical IPublishedServiceStore persistence is not yet wired into AddServerFeatures");
        ResolveImplementationType(services, typeof(IPublishIntentStore))
            .Should().Be<InMemoryPublishIntentStore>(
                "canonical IPublishIntentStore persistence is not yet wired into AddServerFeatures");
        ResolveImplementationType(services, typeof(IDeploymentStore))
            .Should().Be<InMemoryDeploymentStore>(
                "canonical IDeploymentStore persistence is not yet wired into AddServerFeatures");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = BuildBaseServices();
        services.AddMcpOperatorSurface(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private static Type? ResolveImplementationType(IServiceCollection services, Type serviceType)
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == serviceType);
        return descriptor?.ImplementationType;
    }

    private static ServiceCollection BuildBaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IGeoprocessingJobService>());
        return services;
    }
}
