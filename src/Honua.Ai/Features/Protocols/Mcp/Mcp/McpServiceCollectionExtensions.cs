// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.AiBuilder;
using Honua.Ai.AiBuilder.Planning;
using Honua.Ai.Grounding;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Reporting.Abstractions;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// DI registration for the MCP operator surface. Tool and resource handlers are
/// stateless singletons; handlers that need scoped state resolve it through an
/// <see cref="IServiceScopeFactory"/> during the request. That keeps the
/// <c>SurfaceInitialized</c> startup log fired exactly once across the process
/// lifetime and avoids re-building the tool and resource catalogs on every
/// <c>POST /mcp</c> request.
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

        // PlanAnalysisTool resolves its IPlanAnalysisService config-driven:
        // the live (Bedrock-backed) planner when WorkflowGeneration is enabled
        // with a live default provider, otherwise the deterministic fixture
        // replay. The fixture catalog is always registered as the fallback, so
        // CI (no provider configured) stays AI-credit-free. Hosts can still call
        // services.Replace(...) after AddMcpOperatorSurface to force a specific
        // implementation.
        services.AddAiBuilderPlanAnalysis(configuration);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, ValidatePlanTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, DryRunPlanTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, ValidatePackageTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, PreviewPackageTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, ExecutePlanTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, CancelJobTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, ProposeOperationTool>());

        // honua_publish_service (#1951): the authoring/publishing tool. It routes
        // through the canonical IOperationInvoker (operations toolset) →
        // ServicePublishExecutor → ILayerPublishingService rather than
        // reimplementing publish. Registered unconditionally and gated at
        // invocation time: the operations toolset (AddOperationsToolset) is wired
        // later in the server composition root than AddMcpOperatorSurface, so a
        // descriptor-list check here would never see IOperationInvoker. Instead
        // the tool resolves the invoker per-request and returns a structured
        // "unavailable" handle when no operations toolset is composed — the same
        // pattern ProposeOperationTool uses for the operation gateway.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, PublishServiceTool>());

        // Authoring tools (#1951): create_map_package / create_app_package route
        // through the canonical IMapGenerationService / IAppGenerationService
        // generation pipelines. Those services are registered later in the host
        // composition root than AddMcpOperatorSurface, so the tools resolve them
        // per-request (like PublishServiceTool resolves IOperationInvoker) and
        // return a structured capability-unavailable result when no generation
        // service is composed. Registered unconditionally so the catalog is
        // transport-symmetric.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, CreateMapPackageTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, CreateAppPackageTool>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, PlanAnalysisTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, GroundCandidatesTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, ClarifyIntentTool>());

        // honua_list_capabilities (#1949): a self-describing manifest of the live
        // tool/resource surface for a cold client LLM. Registered unconditionally
        // because it only reflects whatever catalog the composition wired — it has
        // no external service dependency and never advertises an empty capability
        // it cannot back.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, Discovery.ListCapabilitiesTool>());

        // Geocode/route tools (#1597) are only advertised when the host
        // composition has wired the underlying canonical services (AddGeocoding
        // / AddRouting run before AddMcpOperatorSurface in the server
        // composition root). Checking the descriptor list here keeps
        // tools/list honest: hosts without the capability never advertise a
        // tool that could only fail at invocation time, and tests that call
        // AddMcpOperatorSurface in isolation keep working.
        if (services.Any(d => d.ServiceType == typeof(Honua.Geocoding.Features.Geocoding.Abstractions.IGeocodeCoordinatorService)))
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, GeocodeTool>());

            // honua_geocode_addresses: batch companion to honua_geocode_address,
            // same canonical coordinator, so the same capability gate applies.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, GeocodeAddressesTool>());
        }

        // honua_ingest_dataset: inline CSV/GeoJSON → catalog table through the
        // canonical IFileImportService pipeline (the REST admin import service).
        // Gated on the import service being wired (the Postgres provider
        // registers it before AddMcpOperatorSurface) so tools/list stays honest
        // in compositions without an import-capable data provider.
        if (services.Any(d => d.ServiceType == typeof(Honua.Core.Features.FileImport.Abstractions.IFileImportService)))
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, IngestDatasetTool>());
        }

        if (services.Any(d => d.ServiceType == typeof(Honua.Routing.Features.Routing.Abstractions.IRoutingProvider)))
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, RouteTool>());
        }

        // Catalog-discovery and feature-query tools (#1xxx) are thin adapters
        // over the canonical Metadata v2 graph and the shared feature-query
        // pipeline. They are only advertised when the host composition has wired
        // IMetadataV2GraphProvider (the same provider that backs /rest/services
        // and FeatureServer query). Gating keeps tools/list honest in tests that
        // call AddMcpOperatorSurface in isolation without a data provider.
        if (services.Any(d => d.ServiceType == typeof(Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphProvider)))
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, MapTools.ListLayersTool>());

            // honua_resolve_entity (#1949): NL/text → ranked service/layer refs over
            // the same Metadata v2 catalog ListLayersTool reads, so it is gated on
            // the same provider being wired.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, Discovery.ResolveEntityTool>());

            if (services.Any(d => d.ServiceType == typeof(Honua.Core.Features.FeatureStore.Abstractions.IFeatureReader)))
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, MapTools.QueryFeaturesTool>());
            }

            // NO feature-mutation tool is registered here BY DESIGN. Honua does
            // not support AI operational data editing: the MCP surface deliberately
            // exposes no feature-edit verb (no honua_edit_features), per ADR-0028
            // (docs/internal/contributor/adr/0028-ai-data-editing-not-allowed.md),
            // founder-reaffirmed 2026-07-06. The shared edit/transaction pipeline
            // (IEditProcessor + IFeatureWriter.ApplyEditsAsync) remains for the
            // human-facing protocol adapters (FeatureServer applyEdits, OGC API
            // Features, WFS Transaction, OData CRUD, admin) — it is intentionally
            // NOT projected onto any AI/MCP tool. Do not reintroduce an MCP edit
            // tool without reversing ADR-0028.

            // The map-render tool additionally needs the canonical raster
            // renderer (the same IRasterMapRenderer the OGC API Maps / MapServer
            // export / WMS GetMap surfaces drive).
            if (services.Any(d => d.ServiceType == typeof(Honua.Core.Features.Raster.Abstractions.IRasterMapRenderer)))
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, MapTools.RenderMapTool>());
            }
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, JobStatusResource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, JobResultsResource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, ProposalStatusResource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, WorkspaceResource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, ProcessCatalogResource>());

        // honua://catalog/features (#1946, ADR-0054): the evidence-based feature
        // catalog. Registered in the default composition (unlike the promotion
        // resources) because it serves a static, embedded, drift-gated artifact —
        // it has no runtime persistence dependency and cannot advertise an empty
        // or stale surface.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, FeatureCatalogResource>());

        // Only advertise the report resource when the host has actually wired
        // IAnalysisReportService. AddAnalysisReporting is the canonical
        // registrar and is gated on Reporting:Enabled; checking for the
        // service here covers both the disabled-feature case and tests that
        // call AddMcpOperatorSurface in isolation.
        if (services.Any(d => d.ServiceType == typeof(IAnalysisReportService)))
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResource, AnalysisReportResource>());
        }

        services.TryAddSingleton<McpOperatorSurface>();

        // MCP transport + session-lifecycle options (A3 hardening; #2537):
        // server-initiated GET-stream toggle, idle TTL, session cap, and eviction
        // policy. Bound from the `Mcp` section; defaults live on McpOptions.
        services.AddOptions<McpOptions>()
            .Bind(configuration.GetSection(McpOptions.SectionName));

        // Streamable-HTTP session registry (honua-server#1954). A process-wide
        // singleton so a session id issued on initialize is recognized on every
        // subsequent POST/GET/DELETE handled by the same host. Constructed via a
        // factory so it picks up the configured lifecycle bounds and the host
        // TimeProvider (defaults to TimeProvider.System when none is registered).
        services.TryAddSingleton(static sp =>
        {
            var options = sp.GetRequiredService<IOptions<McpOptions>>().Value;
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new McpSessionManager(
                options.MaxSessions,
                options.SessionIdleTimeout,
                options.SessionEvictionPolicy,
                timeProvider);
        });

        // Server-push notifications over the session SSE stream (honua-server#1954):
        // the publisher builds notifications/progress + */list_changed frames and
        // enqueues them onto the owning session; the progress bridge polls the
        // canonical job runtime and forwards progress to the session that started
        // the job. Both are process-wide singletons.
        services.TryAddSingleton<IMcpNotificationPublisher, McpNotificationPublisher>();
        services.TryAddSingleton<McpJobProgressBridge>();

        // ADR-0058 B2 (#2334): bind the served /mcp catalog to the unified
        // capability registry as its single source of truth. The registry is the
        // provenance the honua_list_capabilities projection and the geospatial-mcp
        // manifest emitter read; the startup composition check
        // (Capabilities:RegistryBinding, default on) fails fast if the served
        // surface ever diverges from the registry roster.
        services.AddCapabilityRegistry();
        services.AddOptions<CapabilityRegistryBindingOptions>()
            .Bind(configuration.GetSection(CapabilityRegistryBindingOptions.SectionName));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, McpRegistryBindingStartupCheck>());

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
