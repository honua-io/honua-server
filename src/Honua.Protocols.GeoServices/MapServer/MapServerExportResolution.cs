// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    private sealed record MapServerExportRequest(string ServiceId, Dictionary<string, StringValues> Values);
    private sealed record AuthorizedMapServerExport(
        MapServerPublicationSelection Selection, ResourceAccessSet Access, double[] Bounds);

    private static async Task<(AuthorizedMapServerExport? Export, IResult? Error)> ResolveAuthorizedMapServerExportAsync(
        HttpContext context, MapServerExportRequest request, CancellationToken cancellationToken)
    {
        var (bounds, boundsError) = await ResolveMapServerExportBoundsAsync(
            context, request.Values, cancellationToken).ConfigureAwait(false);
        if (boundsError is not null)
        {
            return (null, boundsError);
        }
        var (selection, selectionError) = await ResolveMapServerPublicationSelectionAsync(
            context, request.ServiceId, cancellationToken).ConfigureAwait(false);
        if (selectionError is not null)
        {
            return (null, selectionError);
        }
        var (access, accessError) = await AuthorizeMapServerExportAsync(context, selection!, cancellationToken).ConfigureAwait(false);
        if (accessError is not null)
        {
            return (null, accessError);
        }
        if (!MetadataV2MapServerDrawing.UsesCachedDrawing(selection!.Service))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, "Dynamic MapServer drawing does not provide tile exports."));
        }
        return (new(selection!, access, bounds), null);
    }

    private static async Task<(double[] Bounds, IResult? Error)> ResolveMapServerExportBoundsAsync(
        HttpContext context, Dictionary<string, StringValues> values, CancellationToken cancellationToken)
    {
        if (!TryParseExportTilesExtent(
                GetValue(values, "exportExtent") ?? GetValue(values, "bbox"),
                GetValue(values, "exportExtentSR") ?? GetValue(values, "bboxSR"),
                out var extent, out var srid, out var error))
        {
            return ([], StandardErrorHelpers.CreateBadRequest(context, error ?? "Invalid exportExtent parameter."));
        }
        var transformed = await TryTransformExtentAsync(
            context, extent, srid, SpatialReference.WGS84.Wkid, cancellationToken).ConfigureAwait(false);
        if (!transformed.IsSuccess)
        {
            return ([], StandardErrorHelpers.CreateBadRequest(
                context, transformed.Error ?? "Invalid exportExtent spatial reference."));
        }
        return (NormalizeExportTilesBounds(transformed.Extent), null);
    }
}
