// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.DataEnrichment.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Honua.Server.Features.DataEnrichment;

/// <summary>
/// Data-enrichment API (#374). A thin protocol adapter that lets callers enrich a
/// registered source layer with attributes drawn from a registered enrichment
/// dataset (administrative boundary, POI, or demographic reference layer) keyed by
/// a stable <c>datasetKey</c> instead of an internal layer id.
/// </summary>
/// <remarks>
/// <para>
/// The actual spatial join is delegated to the shared
/// <see cref="Core.Features.SpatialAnalytics.Abstractions.ISpatialAnalyticsReader"/>
/// pipeline (PostGIS <c>ST_Intersects</c>/<c>ST_Within</c>/<c>ST_Contains</c>/
/// <c>ST_DWithin</c>) so behavior, error mapping, and safety caps stay identical to
/// the FeatureServer/OGC <c>spatialJoin</c> surface. This slice only adds the
/// catalog indirection and a clean <c>/api/enrich</c> request shape.
/// </para>
/// <para>
/// MVP deferrals (documented in <c>docs/operator/data-enrichment.md</c>): no
/// bundled third-party demographic datasets, no inline-GeoJSON source feature
/// sets, and no async/batch or CDC-triggered enrichment. Enrichment operates over
/// registered layers only.
/// </para>
/// </remarks>
internal static class DataEnrichmentEndpoints
{
    private const string EnrichmentTag = "Data Enrichment";

    public static IEndpointRouteBuilder MapDataEnrichmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/enrich/catalog", DataEnrichmentRequestHandlers.HandleCatalogGet)
            .WithName("ListEnrichmentCatalog")
            .WithDisplayName("List Enrichment Catalog")
            .WithSummary("List registered enrichment datasets")
            .WithDescription(
                "Returns the operator-curated catalog of enrichment datasets (administrative "
                + "boundary, POI, and demographic reference layers) that callers may reference "
                + "by key from POST /api/enrich.")
            .WithTags(EnrichmentTag)
            .Produces<EnrichmentCatalogResponse>(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status402PaymentRequired)
            .AllowAnonymous();

        // Managed enrichment-dataset catalog discovery (#2280). Edition-filtered: a
        // caller sees only datasets whose minimum edition is at or below theirs.
        endpoints.MapGet("/api/enrich/datasets", (Delegate)DataEnrichmentRequestHandlers.HandleDatasetsGet)
            .WithName("ListEnrichmentDatasets")
            .WithDisplayName("List Enrichment Datasets")
            .WithSummary("Discover managed enrichment datasets")
            .WithDescription(
                "Returns the managed enrichment-dataset catalog (boundary, demographic, and POI "
                + "reference datasets) visible to the caller's edition, with provenance, "
                + "attribution, and license metadata.")
            .WithTags(EnrichmentTag)
            .Produces<EnrichmentDatasetsResponse>(StatusCodes.Status200OK, contentType: "application/json")
            .AllowAnonymous();

        endpoints.MapGet("/api/enrich/datasets/{id}", (Delegate)DataEnrichmentRequestHandlers.HandleDatasetGet)
            .WithName("GetEnrichmentDataset")
            .WithDisplayName("Get Enrichment Dataset")
            .WithSummary("Discover a single managed enrichment dataset")
            .WithDescription("Returns a single managed enrichment dataset by id, filtered by the caller's edition.")
            .WithTags(EnrichmentTag)
            .Produces<EnrichmentDatasetMetadata>(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status404NotFound)
            .AllowAnonymous();

        // Cast to Delegate so the route-handler MapPost overload is selected rather
        // than the RequestDelegate overload (the handler's single-HttpContext /
        // Task<IResult> shape otherwise matches RequestDelegate and trips ASP0016).
        endpoints.MapPost("/api/enrich", (Delegate)DataEnrichmentRequestHandlers.HandleEnrichPost)
            .WithName("EnrichFeatures")
            .WithDisplayName("Enrich Features")
            .WithSummary("Enrich a source layer with attributes from an enrichment dataset")
            .WithDescription(
                "Spatially joins a registered source layer to a registered enrichment dataset and "
                + "returns each source feature enriched with a match count and the dataset's carried "
                + "attributes. Operates over registered layers only.")
            .WithTags(EnrichmentTag)
            .Accepts<EnrichmentRequest>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "application/geo+json")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status402PaymentRequired)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status501NotImplemented)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .AllowAnonymous();

        // Admin registration surface for the managed catalog (#2280). No-ops on
        // non-Postgres profiles where the registry store is unavailable.
        endpoints.MapEnrichmentDatasetAdminEndpoints();

        return endpoints;
    }
}
