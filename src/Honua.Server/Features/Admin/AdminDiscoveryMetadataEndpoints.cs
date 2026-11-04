// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

internal static class AdminDiscoveryMetadataEndpoints
{
    private const int MaxTitleLength = 512;
    private const int MaxDescriptionLength = 16384;
    private const int MaxKeywordLength = 256;
    private const int MaxKeywords = 100;

    public static void MapAdminDiscoveryMetadataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin")
            .WithApiVersionSet().HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata").RequireAdminAuthorization();
        group.MapGet("/metadata/layers/{layerId:int}/discovery", GetLayerAsync)
            .WithName("GetAdminLayerDiscovery");
        group.MapPut("/metadata/layers/{layerId:int}/discovery", UpdateLayerAsync)
            .WithName("UpdateAdminLayerDiscovery");
        group.MapGet("/services/{serviceName}/discovery", GetServiceAsync)
            .WithName("GetAdminServiceDiscovery");
        group.MapPut("/services/{serviceName}/discovery", UpdateServiceAsync)
            .WithName("UpdateAdminServiceDiscovery");
    }

    private static Task<IResult> GetLayerAsync(int layerId, HttpContext context,
        [FromServices] IMetadataV2GraphProvider provider) =>
        ReadAsync(context, provider, snapshot => DiscoveryMetadataTarget.ForLayer(snapshot, layerId));

    private static Task<IResult> GetServiceAsync(string serviceName, HttpContext context,
        [FromServices] IMetadataV2GraphProvider provider) =>
        ReadAsync(context, provider, snapshot => DiscoveryMetadataTarget.ForService(snapshot, serviceName));

    private static Task<IResult> UpdateLayerAsync(int layerId, DiscoveryMetadataUpdateRequest request,
        HttpContext context, [AsParameters] DiscoveryWriteServices services) =>
        WriteAsync(new(context, request, services), snapshot => DiscoveryMetadataTarget.ForLayer(snapshot, layerId));

    private static Task<IResult> UpdateServiceAsync(string serviceName, DiscoveryMetadataUpdateRequest request,
        HttpContext context, [AsParameters] DiscoveryWriteServices services) =>
        WriteAsync(new(context, request, services), snapshot => DiscoveryMetadataTarget.ForService(snapshot, serviceName));

    private static async Task<IResult> ReadAsync(HttpContext context, IMetadataV2GraphProvider provider,
        Func<MetadataV2GraphSnapshot, DiscoveryMetadataTarget?> resolve)
    {
        var snapshot = await provider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        var target = resolve(snapshot);
        return target is null ? Problem(context, 404, "Metadata target not found.") : Respond(snapshot, target);
    }

    private static async Task<IResult> WriteAsync(DiscoveryWrite write,
        Func<MetadataV2GraphSnapshot, DiscoveryMetadataTarget?> resolve)
    {
        var error = Validate(write.Request);
        if (error is not null)
            return Problem(write.Context, 400, error);
        var snapshot = await write.Services.Store.GetCurrentAsync(write.Context.RequestAborted).ConfigureAwait(false);
        var target = resolve(snapshot);
        if (target is null)
            return Problem(write.Context, 404, "Metadata target not found.");
        if (!string.Equals(snapshot.Etag, write.Request.Etag, StringComparison.Ordinal))
            return Problem(write.Context, 409, "Metadata changed. Reload before saving.");
        MetadataV2GraphSnapshot saved;
        try
        {
            saved = await write.Services.Store.SaveAsync(target.Apply(snapshot.Graph, write.Request),
                snapshot.Etag, write.Context.RequestAborted).ConfigureAwait(false);
        }
        catch (MetadataV2GraphConcurrencyException)
        {
            return Problem(write.Context, 409, "Metadata changed. Reload before saving.");
        }
        await write.Services.Cache.InvalidateServiceCatalogAsync(null, null, write.Context.RequestAborted).ConfigureAwait(false);
        return Respond(saved, resolve(saved)!);
    }

    private static string? Validate(DiscoveryMetadataUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Etag))
            return "An ETag from the discovery read is required.";
        if (!ValidText(request.Title, MaxTitleLength) || !ValidText(request.Description, MaxDescriptionLength))
            return "Title and description must be within their length limits and contain no invalid control characters.";
        if (request.Keywords is null || request.Keywords.Length > MaxKeywords ||
            request.Keywords.Any(keyword => string.IsNullOrWhiteSpace(keyword) || !ValidText(keyword, MaxKeywordLength)))
            return "Supply at most 100 nonempty keywords of at most 256 characters each.";
        return null;
    }

    private static bool ValidText(string? value, int limit) => value is null ||
        (value.Length <= limit && !value.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')));

    private static IResult Respond(MetadataV2GraphSnapshot snapshot, DiscoveryMetadataTarget target) =>
        Results.Json(ApiResponse<DiscoveryMetadataResponse>.CreateSuccess(new()
        {
            Etag = snapshot.Etag, EntityId = target.Metadata.Id,
            Title = target.Metadata.Title, Description = target.Metadata.Description,
            Keywords = target.Metadata.Keywords, AffectedPublications = target.Publications
        }), DiscoveryMetadataJsonContext.Default.ApiResponseDiscoveryMetadataResponse);

    private static IResult Problem(HttpContext context, int status, string detail) =>
        ProblemDetailsHelpers.CreateAdminProblem(context, status, detail);

    internal sealed record DiscoveryWriteServices(
        [FromServices] IMetadataV2GraphStore Store,
        [FromServices] OutputCacheInvalidationService Cache);

    private sealed record DiscoveryWrite(HttpContext Context, DiscoveryMetadataUpdateRequest Request, DiscoveryWriteServices Services);
}
