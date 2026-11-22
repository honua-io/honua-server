// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;

namespace Honua.Server.Features.Admin.Exports;

internal readonly record struct ExportZoomBounds(int Lowest = 0, int Highest = 24);

internal sealed record ExportCreatePlan
{
    public TileOperationStartRequest? Start { get; init; }

    public string? Title { get; init; }

    public string? Problem { get; init; }

    public int StatusCode { get; init; }
}

// Turns "export this Layer" into a complete export job: the id defaults to the Layer's name as a slug
// and the identity to the Layer's declared text key, or the object id as text. Both stay overridable.
internal static class ExportCreateDefaults
{
    private const int MaxIdLength = 64;

    public static ExportCreatePlan Resolve(
        ExportCreateRequest request, MetadataV2GraphSnapshot snapshot, ExportZoomBounds? zoomBounds = null)
    {
        var bounds = zoomBounds ?? new ExportZoomBounds();
        var source = request.Source;
        if (source is null || !string.Equals(source.Kind ?? "layer", "layer", StringComparison.Ordinal))
        {
            return Refuse("Only Layer sources can be exported today; upload a file or connect a bucket, then export its Layer.",
                StatusCodes.Status422UnprocessableEntity);
        }

        if (string.IsNullOrWhiteSpace(source.ServiceId) || source.LayerId is null)
        {
            return Refuse("Property 'source' needs 'serviceId' and 'layerId'.", StatusCodes.Status400BadRequest);
        }

        var published = PMTilesProxySourceResolver.ResolveForPublish(snapshot, source.ServiceId, source.LayerId.Value);
        if (published is null)
        {
            return Refuse("The source is not a routable Layer of that Service.", StatusCodes.Status404NotFound);
        }

        var resource = published.Resource;
        var start = BuildStartRequest(request, resource, bounds);
        var title = string.IsNullOrWhiteSpace(resource.Metadata.Title) ? null : resource.Metadata.Title.Trim();
        return ExportRequestRules.Problem(start) is { } problem
            ? Refuse(problem, StatusCodes.Status400BadRequest)
            : new ExportCreatePlan { Start = start, Title = title, StatusCode = StatusCodes.Status202Accepted };
    }

    private static TileOperationStartRequest BuildStartRequest(
        ExportCreateRequest request, MetadataV2Object resource, ExportZoomBounds bounds)
    {
        var zoom = ClampZoom(request.MinZoom, request.MaxZoom, bounds);
        var identity = ExportFields.ChooseIdentity(resource, request.IdentityField);
        return new TileOperationStartRequest
        {
            Operation = TileCacheExecutionSpecBuilder.ExportOperation,
            ServiceId = request.Source!.ServiceId,
            LayerId = request.Source.LayerId,
            ExportId = request.ExportId ?? Slug(resource.Metadata.Name, resource.Metadata.Id),
            IdentityField = identity.Field,
            MinZoom = zoom.MinZoom,
            MaxZoom = zoom.MaxZoom,
            MaxTiles = request.MaxTiles ?? TileCacheJobExecutor.LocalMaxTiles
        };
    }

    // A requested zoom outside the server's tile ceiling is clamped. Clients overzoom past the archive.
    internal static (int? MinZoom, int? MaxZoom) ClampZoom(int? minZoom, int? maxZoom, ExportZoomBounds bounds)
    {
        var min = minZoom is { } low ? Math.Clamp(low, bounds.Lowest, bounds.Highest) : (int?)null;
        return maxZoom is not { } high
            ? (min, null)
            : (min, Math.Clamp(high, min ?? bounds.Lowest, bounds.Highest));
    }

    // Lowercase letters, digits and single hyphens, 3-64 characters, starting with a letter or digit.
    internal static string Slug(string? name, string fallback)
    {
        var slug = new StringBuilder();
        foreach (var character in (string.IsNullOrWhiteSpace(name) ? fallback : name).ToLowerInvariant())
        {
            var next = char.IsAsciiLetterOrDigit(character) ? character : '-';
            if (next != '-' || (slug.Length > 0 && slug[^1] != '-'))
            {
                slug.Append(next);
            }
        }

        var trimmed = slug.ToString().Trim('-');
        trimmed = trimmed.Length > MaxIdLength ? trimmed[..MaxIdLength].TrimEnd('-') : trimmed;
        return trimmed.Length >= 3 ? trimmed : $"export-{trimmed}".TrimEnd('-');
    }

    private static ExportCreatePlan Refuse(string problem, int statusCode) => new() { Problem = problem, StatusCode = statusCode };
}
