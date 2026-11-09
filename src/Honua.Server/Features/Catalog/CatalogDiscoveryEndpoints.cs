// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Catalog;

internal static class CatalogDiscoveryEndpoints
{
    public static IServiceCollection AddCatalogDiscovery(this IServiceCollection services)
    {
        services.AddSingleton<CatalogGraphProjection>();
        services.AddScoped<CatalogDiscoveryReader>();
        return services;
    }

    public static void MapCatalogReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/catalogs").WithApiVersionSet().HasApiVersion(1, 0)
            .WithTags("Catalogs").AllowAnonymous();
        group.MapGet("", ListAsync).WithName("ListCatalogs").Produces<IReadOnlyList<CatalogSummary>>();
        group.MapGet("/{catalogId}", ViewAsync).WithName("GetCatalog").Produces<CatalogView>().ProducesProblem(404);
        group.MapGet("/{catalogId}/items", SearchAsync).WithName("SearchCatalogItems").Produces<CatalogPage>().ProducesProblem(400).ProducesProblem(404);
        group.MapGet("/{catalogId}/items/{kind}", DetailAsync).WithName("GetCatalogItem").Produces<CatalogItem>().ProducesProblem(404);
    }

    private static async Task<IResult> ListAsync(HttpContext context, CatalogDiscoveryReader reader)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(await reader.ListAsync(context).ConfigureAwait(false), CatalogJsonContext.Default.IReadOnlyListCatalogSummary);
    }

    private static async Task<IResult> ViewAsync(HttpContext context, CatalogDiscoveryReader reader)
    {
        context.Response.Headers.CacheControl = "no-store";
        var result = await reader.ReadAsync(context, CatalogId(context)).ConfigureAwait(false);
        return result is null ? NotFound(context) : Results.Json(result.Catalog, CatalogJsonContext.Default.CatalogView);
    }

    private static async Task<IResult> DetailAsync(HttpContext context, CatalogDiscoveryReader reader)
    {
        context.Response.Headers.CacheControl = "no-store";
        var result = await reader.ReadAsync(context, CatalogId(context)).ConfigureAwait(false);
        var id = context.Request.Query["id"].ToString();
        var kind = context.Request.RouteValues["kind"]?.ToString();
        var item = result?.Items.SingleOrDefault(item => item.Kind == kind && item.Id == id);
        return item is null ? NotFound(context) : Results.Json(item, CatalogJsonContext.Default.CatalogItem);
    }

    private static async Task<IResult> SearchAsync(HttpContext context, CatalogDiscoveryReader reader, ICommonQueryValidator validator)
    {
        context.Response.Headers.CacheControl = "no-store";
        var result = await reader.ReadAsync(context, CatalogId(context)).ConfigureAwait(false);
        if (result is null) return NotFound(context);
        var parameters = CatalogSearchParameters.Parse(context.Request.Query);
        if (parameters is null) return StandardErrorHelpers.CreateBadRequest(context, "Invalid catalog search parameters.");
        var bounds = validator.ValidateBbox(parameters.Bbox, 4326);
        if (!bounds.IsValid || !Iso8601TemporalIntervalParser.TryParseRange(parameters.Datetime, out var start, out var end, out _))
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid spatial or temporal filter.");
        var query = parameters.Search with
        {
            Bbox = bounds.Value is { } bbox ? Honua.Core.Features.Shared.Models.BoundingBox.Create(bbox.MinX, bbox.MinY, bbox.MaxX, bbox.MaxY, 4326) : null,
            Time = parameters.Datetime is null ? null : new() { Start = start, End = end }
        };
        return Results.Json(query.Apply(result.Items, result.Catalog), CatalogJsonContext.Default.CatalogPage);
    }

    private static string CatalogId(HttpContext context) => context.Request.RouteValues["catalogId"]?.ToString() ?? string.Empty;
    private static IResult NotFound(HttpContext context) => StandardErrorHelpers.CreateNotFound(context, "Catalog item not found.");
}

