// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp;
using Honua.Server.Features.Mcp.Stores;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Mcp;

/// <summary>
/// Pins the promotion-surface DI wiring to the stub pattern documented in
/// <c>docs/developer/MCP_SERVER.md</c>. Resources are advertised as
/// <c>contract stub</c> while <c>AddMcpOperatorSurface</c> only registers
/// in-memory fallback stores; when canonical publishing/deployment
/// persistence later registers earlier in the composition root, the
/// fallback registrations become no-ops and this test forces the docs flip
/// from <c>contract stub</c> to <c>functional</c>.
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

    private static ServiceProvider BuildProvider()
    {
        var services = BuildBaseServices();
        services.AddMcpOperatorSurface(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private static ServiceCollection BuildBaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IGeoprocessingJobService>());
        return services;
    }
}
