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
        group.MapGet("/metadata/layers/{storageLayerId:int}/discovery", GetLayerAsync)
            .WithName("GetAdminLayerDiscovery");
        group.MapPut("/metadata/layers/{storageLayerId:int}/discovery", UpdateLayerAsync)
            .WithName("UpdateAdminLayerDiscovery");
        group.MapGet("/services/{serviceName}/discovery", GetServiceAsync)
            .WithName("GetAdminServiceDiscovery");
        group.MapPut("/services/{serviceName}/discovery", UpdateServiceAsync)
            .WithName("UpdateAdminServiceDiscovery");
    }

    private static Task<IResult> GetLayerAsync(int storageLayerId, HttpContext context,
        [FromServices] IMetadataV2GraphProvider provider) =>
        ReadAsync(context, provider, snapshot => DiscoveryMetadataTarget.ForLayer(snapshot, storageLayerId));

    private static Task<IResult> GetServiceAsync(string serviceName, HttpContext context,
        [FromServices] IMetadataV2GraphProvider provider) =>
        ReadAsync(context, provider, snapshot => DiscoveryMetadataTarget.ForService(snapshot, serviceName));

    private static Task<IResult> UpdateLayerAsync(int storageLayerId, DiscoveryMetadataUpdateRequest request, HttpContext context) =>
        WriteAsync(context, request, snapshot => DiscoveryMetadataTarget.ForLayer(snapshot, storageLayerId));

    private static Task<IResult> UpdateServiceAsync(string serviceName, DiscoveryMetadataUpdateRequest request, HttpContext context) =>
        WriteAsync(context, request, snapshot => DiscoveryMetadataTarget.ForService(snapshot, serviceName));

    private static async Task<IResult> ReadAsync(HttpContext context, IMetadataV2GraphProvider provider,
        Func<MetadataV2GraphSnapshot, DiscoveryMetadataTarget?> resolve)
    {
        var snapshot = await provider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        var target = resolve(snapshot);
        return target is null ? Problem(context, 404, "Metadata target not found.") : Respond(snapshot, target);
    }

    private static async Task<IResult> WriteAsync(HttpContext context, DiscoveryMetadataUpdateRequest request,
        Func<MetadataV2GraphSnapshot, DiscoveryMetadataTarget?> resolve)
    {
        var error = Validate(request);
        if (error is not null)
            return Problem(context, 400, error);
        var store = context.RequestServices.GetRequiredService<IMetadataV2GraphStore>();
        var snapshot = await store.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        var target = resolve(snapshot);
        if (target is null)
            return Problem(context, 404, "Metadata target not found.");
        if (!string.Equals(snapshot.Etag, request.Etag, StringComparison.Ordinal))
            return Problem(context, 409, "Metadata changed. Reload before saving.");
        MetadataV2GraphSnapshot saved;
        try
        {
            saved = await store.SaveAsync(target.Apply(snapshot.Graph, request),
                snapshot.Etag, context.RequestAborted).ConfigureAwait(false);
        }
        catch (MetadataV2GraphConcurrencyException)
        {
            return Problem(context, 409, "Metadata changed. Reload before saving.");
        }
        var cache = context.RequestServices.GetRequiredService<OutputCacheInvalidationService>();
        await cache.InvalidateServiceCatalogAsync(null, null, context.RequestAborted).ConfigureAwait(false);
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
            Etag = snapshot.Etag,
            EntityId = target.Metadata.Id,
            Title = target.Metadata.Title,
            Description = target.Metadata.Description,
            Keywords = target.Metadata.Keywords,
            AffectedPublications = target.Publications
        }), DiscoveryMetadataJsonContext.Default.ApiResponseDiscoveryMetadataResponse);

    private static IResult Problem(HttpContext context, int status, string detail) =>
        ProblemDetailsHelpers.CreateAdminProblem(context, status, detail);

}
