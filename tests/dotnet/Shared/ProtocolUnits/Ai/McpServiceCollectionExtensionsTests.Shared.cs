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
