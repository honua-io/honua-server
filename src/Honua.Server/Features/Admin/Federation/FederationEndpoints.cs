// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Federation.Abstractions;
using Honua.Core.Features.Federation.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Admin.Federation.Models;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin.Federation;

/// <summary>
/// Log category for federation admin endpoints.
/// </summary>
internal sealed class FederationEndpointsLog;

/// <summary>
/// Admin endpoints for federated-query source configuration and query-plan inspection
/// (issue #341). The list endpoint exposes the configured sources (no credentials); the
/// plan endpoint runs the offline <see cref="IFederationQueryPlanner"/> against a sample
/// query so operators can see which predicates push down to a remote source versus which
/// are refined locally — without contacting the remote source.
/// </summary>
internal static class FederationEndpoints
{
    /// <summary>
    /// Maps the federation admin endpoints.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    public static void MapFederationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/federation/sources")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Federation")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleListSources)
            .WithDisplayName("List Federated Sources")
            .WithSummary("List configured federated sources (cross-instance and external proxy)");

        group.MapGet("/{id}/plan", HandlePlanQuery)
            .WithDisplayName("Plan Federated Query")
            .WithSummary("Show push-down vs local-refinement plan for a sample query against a source");
    }

    private static IResult HandleListSources(
        [FromServices] IFederationSourceRegistry registry,
        [FromServices] ILogger<FederationEndpointsLog> logger)
    {
        var sources = registry.GetSources();
        var responses = sources.Select(ToResponse).ToArray();

        FederationLog.SourcesListed(logger, responses.Length);

        return Results.Json(responses, FederationJsonContext.Default.FederationSourceResponseArray);
    }

    private static IResult HandlePlanQuery(
        string id,
        [FromServices] IFederationSourceRegistry registry,
        [FromServices] IFederationQueryPlanner planner,
        [FromServices] ILogger<FederationEndpointsLog> logger,
        string? where = null,
        bool bbox = false,
        bool joinLocal = false)
    {
        if (!registry.TryGetSource(id, out var source))
        {
            return TypedResults.NotFound();
        }

        var query = BuildSampleQuery(where, bbox);
        var plan = planner.Plan(source, in query, joinLocal);

        FederationLog.QueryPlanned(logger, source.Id, plan.RequiresLocalRefinement, plan.RequiresLocalJoin);

        var response = new FederationQueryPlanResponse
        {
            SourceId = plan.SourceId,
            SampleWhere = where,
            SampleBbox = bbox,
            Decisions = plan.Decisions
                .Select(static d => new FederationPredicateDecisionResponse
                {
                    Predicate = d.Predicate.ToString(),
                    PushedDown = d.PushedDown,
                    Reason = d.Reason,
                })
                .ToArray(),
            RequiresLocalRefinement = plan.RequiresLocalRefinement,
            RequiresLocalJoin = plan.RequiresLocalJoin,
        };

        return Results.Json(response, FederationJsonContext.Default.FederationQueryPlanResponse);
    }

    private static FeatureQuery BuildSampleQuery(string? where, bool bbox)
    {
        var query = new FeatureQuery { Where = string.IsNullOrWhiteSpace(where) ? null : where };

        if (bbox)
        {
            // A simple axis-aligned envelope-intersects predicate: the canonical representation
            // of a viewport bbox. WKB content is irrelevant to planning, which keys off the
            // spatial relationship only, so an empty geometry keeps the endpoint side-effect free.
            query = query with
            {
                SpatialFilter = new SpatialFilter
                {
                    Geometry = Array.Empty<byte>(),
                    SpatialRelationship = SpatialRelationship.EnvelopeIntersects,
                    IsSimpleEnvelope = true,
                },
            };
        }

        return query;
    }

    private static FederationSourceResponse ToResponse(FederatedSourceDescriptor source) => new()
    {
        Id = source.Id,
        DisplayName = source.DisplayName,
        Kind = source.Kind.ToString(),
        Endpoint = source.Endpoint.ToString(),
        RemoteLayer = source.RemoteLayer,
        RequestTimeoutSeconds = (int)source.RequestTimeout.TotalSeconds,
        Enabled = source.Enabled,
    };
}
