// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Mcp.Resources;
using Honua.Server.Features.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Mcp;

/// <summary>
/// DI registration for the MCP operator surface. Adds tool and resource
/// handlers as scoped services (so loggers bind correctly) and the operator
/// surface as a singleton since it holds only immutable descriptors.
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

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMcpTool, ValidatePlanTool>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMcpTool, DryRunPlanTool>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMcpTool, ExecutePlanTool>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMcpTool, CancelJobTool>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMcpTool, PlanAnalysisTool>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMcpTool, GroundCandidatesTool>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMcpTool, ClarifyIntentTool>());

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMcpResource, JobStatusResource>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMcpResource, JobResultsResource>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMcpResource, WorkspaceResource>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMcpResource, ProcessCatalogResource>());

        services.TryAddScoped<McpOperatorSurface>();

        return services;
    }
}
