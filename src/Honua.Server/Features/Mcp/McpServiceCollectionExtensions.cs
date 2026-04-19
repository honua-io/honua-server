// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Mcp.Resources;
using Honua.Server.Features.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Mcp;

/// <summary>
/// DI registration for the MCP operator surface. Tool and resource handlers,
/// and the dispatcher itself, are all stateless and depend only on singleton
/// services (<see cref="Geoprocessing.IGeoprocessingJobService"/> and
/// <see cref="ILogger{TCategoryName}"/>), so they are registered as singletons.
/// That keeps the <c>SurfaceInitialized</c> startup log fired exactly once
/// across the process lifetime and avoids re-building the tool and resource
/// catalogs on every <c>POST /mcp</c> request.
/// </summary>
internal static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Registers MCP tools, resources, and the JSON-RPC dispatcher.
    /// </summary>
    public static IServiceCollection AddMcpOperatorSurface(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, ValidatePlanTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, DryRunPlanTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, ExecutePlanTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, CancelJobTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, PlanAnalysisTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, GroundCandidatesTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, ClarifyIntentTool>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, JobStatusResource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, JobResultsResource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, WorkspaceResource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, ProcessCatalogResource>());

        services.TryAddSingleton<McpOperatorSurface>();

        return services;
    }
}
