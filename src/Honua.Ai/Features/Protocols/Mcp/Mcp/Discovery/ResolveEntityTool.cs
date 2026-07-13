// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Discovery;

/// <summary>
/// MCP tool that resolves a natural-language name or phrase to concrete catalog
/// entity references (services and layers) so a client LLM can ground an entity
/// it heard from the user before composing a workflow (#1949). It is a thin,
/// deterministic adapter over the canonical
/// <see cref="IMetadataV2GraphProvider"/> graph — the same metadata snapshot that
/// backs <c>/rest/services</c>, <c>honua_list_layers</c>, FeatureServer, and the
/// feature catalog — so resolution grounds only on capabilities that exist (no
/// hallucinated layers). The returned <c>serviceId</c>/<c>layerId</c> pairs feed
/// directly into <c>honua_query_features</c> and <c>honua_render_map</c>.
/// </summary>
/// <remarks>
/// This is a Honua extension over the bare geospatial-mcp taxonomy: the standard
/// models entity resolution as a CapabilityCatalog/grounding read; the reference
/// implementation exposes it as a first-class tool. A vendored conformance schema
/// is shipped under <c>ConformanceSchemas/geospatial-mcp/tools/resolve_entity.schema.json</c>.
/// </remarks>
internal sealed class ResolveEntityTool : IMcpTool
{
    /// <summary>The tool name published in <c>tools/list</c>.</summary>
    public const string ToolName = "honua_resolve_entity";

    private const string EntityTypeAny = "any";
    private const string EntityTypeService = "service";
    private const string EntityTypeLayer = "layer";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<ResolveEntityTool> _logger;

    public ResolveEntityTool(IGeoprocessingJobService jobService, ILogger<ResolveEntityTool> logger)
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
        Title = "Resolve entity",
        Description = "Routing: a NAME or phrase -> layer references; use this when you just need to find a layer (not to explore or plan an analysis). Resolves a natural-language name or phrase (e.g. \"parcels\", \"flood zones\") to concrete published service/layer references, ranked by match quality. Use the returned serviceId/layerId with honua_query_features and honua_render_map. Resolves only against the live catalog, so it never invents layers that do not exist.",
        InputSchema = DiscoveryToolSchemas.ResolveEntityArgumentSchema,
        OutputSchema = DiscoveryToolSchemas.ResolveEntityOutputSchema,
        Annotations = McpToolAnnotationSets.ReadOnly("Resolve entity")
    };

    /// <inheritdoc />
    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("ResolveEntity");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Catalog, OperatorOperation.Discover, cancellationToken)
            .ConfigureAwait(false);

        var argument = McpToolHelpers.ParseArguments(arguments, DiscoveryJsonContext.Default.McpResolveEntityArgument);
        var text = argument.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new GeoprocessingValidationException("'text' is required and must be a non-empty string.");
        }

        var entityType = NormalizeEntityType(argument.EntityType);
        var limit = NormalizeLimit(argument.Limit);

        var graphProvider = httpContext.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var matches = new List<McpEntityMatch>();
        foreach (var service in snapshot.Graph.Services)
        {
            if (entityType is EntityTypeAny or EntityTypeService)
            {
                var serviceScore = Score(text, service.Metadata.Name, service.Metadata.Title, service.Metadata.Id);
                if (serviceScore > 0)
                {
                    matches.Add(new McpEntityMatch
                    {
                        Kind = EntityTypeService,
                        ServiceId = service.Metadata.Id,
                        ServiceName = service.Metadata.Name,
                        Name = !string.IsNullOrWhiteSpace(service.Metadata.Title)
                            ? service.Metadata.Title!
                            : service.Metadata.Name,
                        Description = service.Metadata.Description,
                        Score = serviceScore
                    });
                }
            }

            if (entityType is EntityTypeAny or EntityTypeLayer)
            {
                AddLayerMatches(snapshot, service, text, matches);
            }
        }

        matches.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var byService = string.CompareOrdinal(a.ServiceName, b.ServiceName);
            return byService != 0 ? byService : Nullable.Compare(a.LayerId, b.LayerId);
        });

        var page = matches.Count > limit ? matches.GetRange(0, limit) : matches;
        var output = new McpResolveEntityOutput
        {
            Query = text,
            EntityType = entityType,
            MatchCount = page.Count,
            Matches = page
        };

        return McpToolHelpers.SuccessResult(output, DiscoveryJsonContext.Default.McpResolveEntityOutput);
    }

    private static void AddLayerMatches(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        string text,
        List<McpEntityMatch> matches)
    {
        foreach (var publication in snapshot.PublicationsForService(service.Metadata.Id))
        {
            var resource = snapshot.ResolveResource(publication);
            if (resource is null || publication.LayerIndex is not { } layerIndex)
            {
                continue;
            }

            var layerName = !string.IsNullOrWhiteSpace(publication.TitleOverride)
                ? publication.TitleOverride!
                : (!string.IsNullOrWhiteSpace(resource.Metadata.Title)
                    ? resource.Metadata.Title!
                    : resource.Metadata.Name);

            var score = Score(text, layerName, resource.Metadata.Name, resource.Metadata.Description);
            if (score <= 0)
            {
                continue;
            }

            matches.Add(new McpEntityMatch
            {
                Kind = EntityTypeLayer,
                ServiceId = service.Metadata.Id,
                ServiceName = service.Metadata.Name,
                LayerId = layerIndex,
                Name = layerName,
                Type = resource.Type.ToString(),
                GeometryType = (resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None).ToString(),
                Description = resource.Metadata.Description,
                Score = score
            });
        }
    }

    /// <summary>
    /// Deterministic lexical match score in (0, 1]. Exact (case-insensitive) name
    /// match scores highest, then prefix, then substring, then token overlap.
    /// Returns 0 when no candidate field matches so the entity is excluded.
    /// </summary>
    private static double Score(string query, params string?[] candidates)
    {
        var best = 0d;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var value = candidate.Trim();
            if (string.Equals(value, query, StringComparison.OrdinalIgnoreCase))
            {
                return 1.0;
            }

            if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                best = Math.Max(best, 0.85);
            }
            else if (value.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                best = Math.Max(best, 0.6);
            }
            else if (TokenOverlap(query, value))
            {
                best = Math.Max(best, 0.4);
            }
        }

        return best;
    }

    private static bool TokenOverlap(string query, string value)
        => query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Length >= 3 && value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeEntityType(string? entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType))
        {
            return EntityTypeAny;
        }

        var normalized = entityType.Trim().ToLowerInvariant();
        return normalized switch
        {
            EntityTypeService => EntityTypeService,
            EntityTypeLayer => EntityTypeLayer,
            EntityTypeAny => EntityTypeAny,
            _ => throw new GeoprocessingValidationException(
                $"Unsupported entityType '{entityType}'. Expected one of: any, service, layer.")
        };
    }

    private static int NormalizeLimit(int? limit)
    {
        if (limit is not { } value)
        {
            return DiscoveryToolSchemas.DefaultResolveLimit;
        }

        if (value < 1)
        {
            throw new GeoprocessingValidationException("'limit' must be a positive integer.");
        }

        return Math.Min(value, DiscoveryToolSchemas.MaxResolveLimit);
    }
}
