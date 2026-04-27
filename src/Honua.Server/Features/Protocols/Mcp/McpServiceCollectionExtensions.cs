// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Reporting;
using Honua.Server.Features.Grounding;
using Honua.Server.Features.Protocols.Mcp.Resources;
using Honua.Server.Features.Protocols.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Protocols.Mcp;

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
    /// Registers MCP tools, core resources (jobs, workspaces, catalog), and the
    /// JSON-RPC dispatcher. The hosted-promotion surface (published services,
    /// deployments, map/app packages) is intentionally not registered here —
    /// those handlers depend on canonical <see cref="Honua.Core.Features.Publishing.Abstractions.IPublishedServiceStore"/>
    /// and <see cref="Honua.Core.Features.Deployment.Abstractions.IDeploymentStore"/> persistence,
    /// which is not yet wired by the default composition. Hosts that have
    /// registered canonical persistence call <see cref="AddMcpPromotionSurface"/>
    /// to advertise those resources.
    /// </summary>
    public static IServiceCollection AddMcpOperatorSurface(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddGroundingServices(configuration);

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

        if (configuration.GetSection(ReportingConfiguration.SectionName).GetValue("Enabled", defaultValue: true))
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, AnalysisReportResource>());
        }

        services.TryAddSingleton<McpOperatorSurface>();

        return services;
    }

    /// <summary>
    /// Registers the hosted-promotion MCP resource handlers (published services,
    /// deployments, map/app packages, and the promotion list index). Callers
    /// must register canonical <see cref="Honua.Core.Features.Publishing.Abstractions.IPublishedServiceStore"/>
    /// and <see cref="Honua.Core.Features.Deployment.Abstractions.IDeploymentStore"/> persistence
    /// before invoking this extension; this method deliberately does not wire
    /// any fallback stores so the default server composition cannot advertise
    /// promotion resources backed by process-local, always-empty state.
    /// </summary>
    public static IServiceCollection AddMcpPromotionSurface(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, PublishedServiceResource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, DeploymentResource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, MapPackageResource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, AppPackageResource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, PromotionSurfaceIndexResource>());

        return services;
    }
}
