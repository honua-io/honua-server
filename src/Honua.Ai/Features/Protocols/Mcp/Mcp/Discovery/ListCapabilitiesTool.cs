// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Capabilities;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Discovery;

/// <summary>
/// MCP tool that returns a self-describing manifest of the tools, resources, and
/// grounding resources the live <c>/mcp</c> surface exposes (#1949), so a client
/// LLM with no Honua-specific knowledge can discover the full composable surface
/// and plan a workflow. It projects the same in-process catalog the dispatcher
/// serves over both the HTTP-SSE and stdio transports (#1950), so the manifest is
/// transport-symmetric by construction.
/// </summary>
/// <remarks>
/// This is a Honua extension over the bare geospatial-mcp taxonomy (which models
/// capability discovery as a CapabilityCatalog read); the reference implementation
/// exposes it as a first-class tool. A vendored conformance schema is shipped under
/// <c>ConformanceSchemas/geospatial-mcp/tools/list_capabilities.schema.json</c>.
/// </remarks>
internal sealed class ListCapabilitiesTool : IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_list_capabilities";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<ListCapabilitiesTool> _logger;

    public ListCapabilitiesTool(IGeoprocessingJobService jobService, ILogger<ListCapabilitiesTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

    /// <inheritdoc />
    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "List capabilities",
        Description = "List every tool and resource this server exposes, with LLM-grade descriptions and read/write hints, plus the grounding resource URIs to read first, and the named server-authored workflow views that narrow discovery to one bounded journey. Call this first to discover the full workflow surface (discover, ground, query, analyze, compose, publish) before composing a plan; then, if a view matches your task, re-list tools with that view to work from a smaller, server-selected set.",
        InputSchema = DiscoveryToolSchemas.ListCapabilitiesArgumentSchema,
        OutputSchema = DiscoveryToolSchemas.ListCapabilitiesOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("List capabilities")
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ListCapabilities");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Catalog, OperatorOperation.Discover, cancellationToken)
            .ConfigureAwait(false);

        var includeResources = true;
        var includeGroundingResources = true;
        if (arguments is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
        {
            var argument = McpToolHelpers.ParseArguments(arguments, DiscoveryJsonContext.Default.McpListCapabilitiesArgument);
            includeResources = argument.IncludeResources ?? true;
            includeGroundingResources = argument.IncludeGroundingResources ?? true;
        }

        var serverInfo = new McpServerInfo();
        var output = new McpListCapabilitiesOutput
        {
            ServerName = serverInfo.Name,
            ServerVersion = serverInfo.Version,
            ProtocolVersions = McpDataAccessSurface.SupportedProtocolVersions
        };

        // Project the live in-process catalog. Resolved leniently so a lightweight
        // host that did not register the surface still returns a valid (if empty)
        // manifest rather than throwing.
        var surface = httpContext.RequestServices.GetService<McpDataAccessSurface>();
        if (surface is not null)
        {
            // The unified capability registry (ADR-0058 B2, #2334) is the source of
            // truth for the /mcp catalog roster: when present it governs which
            // tools/resources are advertised, while the live handlers still supply
            // the LLM-grade descriptions and read/write hints the wire carries. A
            // lightweight host that did not register the registry falls back to the
            // live surface unchanged.
            var registry = httpContext.RequestServices.GetService<ICapabilityRegistry>();
            output.Tools = await BuildToolManifestAsync(surface, registry, cancellationToken).ConfigureAwait(false);
            output.ToolCount = output.Tools.Count;

            // honua-server#3428: advertise the server-authored workflow discovery
            // views alongside the full catalog, projected from the same live
            // roster. This is the discovery half of the negotiation contract — the
            // client learns the view names, revisions, and digests from the server
            // instead of hard-coding them.
            output.WorkflowViews =
                await BuildWorkflowViewSummariesAsync(surface, cancellationToken).ConfigureAwait(false);

            if (includeResources || includeGroundingResources)
            {
                var resources = BuildResourceManifest(surface, registry);

                if (includeResources)
                {
                    output.Resources = resources;
                    output.ResourceCount = resources.Count;
                }

                if (includeGroundingResources)
                {
                    output.GroundingResources = resources
                        .Where(r => string.Equals(r.Family, McpTelemetry.ResourceFamily.Catalog, StringComparison.Ordinal))
                        .Select(r => r.Uri)
                        .ToList();
                }
            }
        }

        return McpToolHelpers.SuccessResult(output, DiscoveryJsonContext.Default.McpListCapabilitiesOutput);
    }

    /// <summary>
    /// Projects every published workflow view over the live catalog and returns
    /// its summary (honua-server#3428). Deriving the summaries here — rather than
    /// storing them — is what guarantees the advertised member counts, digests,
    /// and byte measurements describe the surface this host actually serves.
    /// </summary>
    private static async Task<List<Views.McpWorkflowViewSummary>> BuildWorkflowViewSummariesAsync(
        McpDataAccessSurface surface,
        CancellationToken cancellationToken)
    {
        var entries = await surface.GetCatalogEntriesAsync(cancellationToken).ConfigureAwait(false);
        var catalog = entries.Select(e =>
            (Views.McpWorkflowViewDescriptorClassifier.Describe(e.Tool), e.IsDynamic)).ToArray();

        var summaries = new List<Views.McpWorkflowViewSummary>(Views.McpWorkflowViewCatalog.Names.Count);
        foreach (var name in Views.McpWorkflowViewCatalog.Names)
        {
            var definition = Views.McpWorkflowViewCatalog.All[name];
            summaries.Add(Views.McpWorkflowViewWire.BuildSummary(
                Views.McpWorkflowViewProjector.Project(definition, catalog)));
        }

        return summaries;
    }

    private static async Task<List<McpCapabilityTool>> BuildToolManifestAsync(
        McpDataAccessSurface surface,
        ICapabilityRegistry? registry,
        CancellationToken cancellationToken)
    {
        var registryToolNames = registry is null
            ? null
            : registry.All
                .Where(d => d.McpToolName is not null)
                .Select(d => d.McpToolName!)
                .ToHashSet(StringComparer.Ordinal);

        var tools = new List<McpCapabilityTool>();
        foreach (var tool in await surface.GetAllToolsAsync(cancellationToken).ConfigureAwait(false))
        {
            // Only advertise tools with capability-registry provenance. The startup
            // composition check (Capabilities:RegistryBinding) fails fast if the
            // served surface ever contains a tool the registry does not describe,
            // so in a conformant host this filter drops nothing.
            if (registryToolNames is not null && !registryToolNames.Contains(tool.Name))
            {
                continue;
            }

            var descriptor = tool.Describe();
            tools.Add(new McpCapabilityTool
            {
                Name = descriptor.Name,
                Title = descriptor.Title,
                Description = descriptor.Description,
                ReadOnly = descriptor.Annotations?.ReadOnlyHint ?? false,
                WorkflowFamily = tool.WorkflowFamily
            });
        }

        tools.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return tools;
    }

    private static List<McpCapabilityResource> BuildResourceManifest(
        McpDataAccessSurface surface,
        ICapabilityRegistry? registry)
    {
        var registryFamilies = registry is null
            ? null
            : registry.All
                .Where(d => d.Id.StartsWith(CapabilityRegistry.McpResourceIdPrefix, StringComparison.Ordinal))
                .Select(d => d.Id[CapabilityRegistry.McpResourceIdPrefix.Length..])
                .ToHashSet(StringComparer.Ordinal);

        var resources = new List<McpCapabilityResource>();
        foreach (var resource in surface.Resources)
        {
            // Only advertise resource families with capability-registry provenance
            // (see BuildToolManifest); the registry-binding startup check guarantees
            // the served families are a subset of the registry roster.
            if (registryFamilies is not null && !registryFamilies.Contains(resource.Family))
            {
                continue;
            }

            foreach (var descriptor in resource.Describe())
            {
                resources.Add(new McpCapabilityResource
                {
                    Uri = descriptor.Uri,
                    Name = descriptor.Name,
                    Description = descriptor.Description,
                    Family = resource.Family
                });
            }

            foreach (var template in resource.DescribeTemplates())
            {
                resources.Add(new McpCapabilityResource
                {
                    Uri = template.UriTemplate,
                    Name = template.Name,
                    Description = template.Description,
                    Family = resource.Family
                });
            }
        }

        resources.Sort(static (a, b) => string.CompareOrdinal(a.Uri, b.Uri));
        return resources;
    }
}
