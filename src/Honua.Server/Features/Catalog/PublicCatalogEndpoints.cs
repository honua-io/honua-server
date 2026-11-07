// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Catalog;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Services;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Capabilities;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Catalog;

internal static class PublicCatalogEndpoints
{
    public static void MapPublicCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/catalog").WithApiVersionSet().HasApiVersion(1, 0)
            .WithTags("Catalog").AllowAnonymous();
        group.MapGet("", ListAsync).WithName("GetPublicCatalog").Produces<PublishedCatalogPage>().ProducesProblem(400);
        group.MapGet("/{id}", DetailAsync).WithName("GetPublicCatalogItem").Produces<PublishedCatalogItem>().ProducesProblem(404);
    }

    private static async Task<IResult> ListAsync(HttpContext context, [AsParameters] PublicCatalogParameters parameters,
        PublicCatalogReader reader, ICommonQueryValidator validator)
    {
        context.Response.Headers.CacheControl = "no-store";
        var bounds = validator.ValidateBbox(parameters.Bbox, 4326);
        if (!bounds.IsValid)
        {
            return StandardErrorHelpers.CreateBadRequest(context, bounds.ErrorMessage ?? "Invalid bbox.");
        }
        if (!Iso8601TemporalIntervalParser.TryParseRange(parameters.Datetime, out var start, out var end, out var timeError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, timeError ?? "Invalid datetime.");
        }
        if (parameters.Offset < 0 || parameters.Limit is < 1 or > PublishedCatalogQuery.MaximumPageSize)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Offset must be nonnegative and limit must be between 1 and 200.");
        }
        var query = new PublishedCatalogQuery
        {
            Text = parameters.Q, Type = parameters.Type, Protocol = parameters.Protocol, Folder = parameters.Folder,
            Tag = parameters.Tag, Owner = parameters.Owner, Bbox = bounds.Value is { } bbox ? Honua.Core.Features.Shared.Models.BoundingBox.Create(bbox.MinX, bbox.MinY, bbox.MaxX, bbox.MaxY, 4326) : null,
            Time = parameters.Datetime is null ? null : new() { Start = start, End = end },
            Offset = parameters.Offset, Limit = parameters.Limit
        };
        var items = await reader.ReadAsync(context).ConfigureAwait(false);
        return Results.Json(query.Apply(items), PublicCatalogJsonContext.Default.PublishedCatalogPage);
    }

    private static async Task<IResult> DetailAsync(HttpContext context, string id, PublicCatalogReader reader)
    {
        context.Response.Headers.CacheControl = "no-store";
        var items = await reader.ReadAsync(context).ConfigureAwait(false);
        var item = items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        return item is null ? StandardErrorHelpers.CreateNotFound(context, "Catalog item not found.")
            : Results.Json(item, PublicCatalogJsonContext.Default.PublishedCatalogItem);
    }
}

internal sealed class PublicCatalogReader(IMetadataV2GraphProvider graphProvider, PublishedCatalogProjection projection,
    ICapabilityManifestService manifestService, ITenantContext tenantContext)
{
    public async Task<IReadOnlyList<PublishedCatalogItem>> ReadAsync(HttpContext context)
    {
        var request = new CapabilityManifestRequest(context.User, tenantContext.TenantId, tenantContext.Source,
            null, null, context.User.Identity?.IsAuthenticated == true);
        var manifest = await manifestService.GetManifestAsync(request, context.RequestAborted).ConfigureAwait(false);
        var snapshot = await graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        return await projection.ProjectAsync(new(snapshot, context.User, BaseUrlResolver.GetBaseUrl(context)),
            manifest.ProtocolEndpoints, context.RequestAborted).ConfigureAwait(false);
    }
}

internal sealed class PublicCatalogParameters
{
    [FromQuery] public string? Q { get; init; }
    [FromQuery] public string? Type { get; init; }
    [FromQuery] public string? Protocol { get; init; }
    [FromQuery] public string? Folder { get; init; }
    [FromQuery] public string? Tag { get; init; }
    [FromQuery] public string? Owner { get; init; }
    [FromQuery] public string? Bbox { get; init; }
    [FromQuery] public string? Datetime { get; init; }
    [FromQuery] public int Offset { get; init; }
    [FromQuery] public int Limit { get; init; } = 50;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PublishedCatalogPage))]
[JsonSerializable(typeof(PublishedCatalogItem))]
internal sealed partial class PublicCatalogJsonContext : JsonSerializerContext;
