// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Classic.Wcs20;

/// <summary>
/// Maps OGC WCS 2.0.1 KVP endpoints.
/// </summary>
internal static class Wcs20Endpoints
{
    /// <summary>
    /// Maps WCS 2.0.1 endpoints for layer-scoped ImageServer and service-scoped OGC aliases.
    /// </summary>
    internal static IEndpointRouteBuilder MapWcs20Endpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/rest/services/{id:int}/ImageServer/WCS",
                static (HttpContext context, int id, Wcs20Handler handler) =>
                    handler.HandleAsync(context, Wcs20RouteScope.ForLayer(id)))
            .WithDisplayName("WCS 2.0.1 ImageServer Service")
            .WithName("ImageServerWcs20")
            .WithSummary("OGC Web Coverage Service 2.0.1")
            .WithDescription("Provides WCS 2.0.1 GetCapabilities, DescribeCoverage, and GetCoverage operations for a raster-backed layer")
            .WithTags("WCS", "OGC", "ImageServer")
            .Produces(StatusCodes.Status200OK, contentType: Wcs20Utilities.XmlContentType)
            .Produces(StatusCodes.Status200OK, contentType: Wcs20Utilities.TiffContentType)
            .Produces(StatusCodes.Status200OK, contentType: Wcs20Utilities.PngContentType)
            .Produces(StatusCodes.Status200OK, contentType: Wcs20Utilities.JpegContentType)
            .Produces(StatusCodes.Status400BadRequest, contentType: Wcs20Utilities.XmlContentType)
            .Produces(StatusCodes.Status404NotFound, contentType: Wcs20Utilities.XmlContentType)
            .Produces(StatusCodes.Status501NotImplemented, contentType: Wcs20Utilities.XmlContentType)
            .CacheOutput(policy => policy.NoCache());

        endpoints.MapGet("/ogc/services/{serviceId}/wcs",
                static (HttpContext context, string serviceId, Wcs20Handler handler) =>
                    handler.HandleAsync(context, Wcs20RouteScope.ForService(serviceId)))
            .WithDisplayName("WCS 2.0.1 Service")
            .WithName("OgcServiceWcs20")
            .WithSummary("OGC Web Coverage Service 2.0.1")
            .WithDescription("Service-scoped WCS 2.0.1 endpoint for raster-backed coverages")
            .WithTags("WCS", "OGC")
            .Produces(StatusCodes.Status200OK, contentType: Wcs20Utilities.XmlContentType)
            .Produces(StatusCodes.Status200OK, contentType: Wcs20Utilities.TiffContentType)
            .Produces(StatusCodes.Status200OK, contentType: Wcs20Utilities.PngContentType)
            .Produces(StatusCodes.Status200OK, contentType: Wcs20Utilities.JpegContentType)
            .Produces(StatusCodes.Status400BadRequest, contentType: Wcs20Utilities.XmlContentType)
            .Produces(StatusCodes.Status404NotFound, contentType: Wcs20Utilities.XmlContentType)
            .Produces(StatusCodes.Status501NotImplemented, contentType: Wcs20Utilities.XmlContentType)
            .CacheOutput(policy => policy.NoCache());

        return endpoints;
    }
}

internal readonly record struct Wcs20RouteScope(int? LayerId, string? ServiceId)
{
    internal bool IsLayerScoped => LayerId.HasValue;

    internal string DisplayName => IsLayerScoped
        ? $"layer:{LayerId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        : $"service:{ServiceId}";

    internal static Wcs20RouteScope ForLayer(int layerId) => new(layerId, null);

    internal static Wcs20RouteScope ForService(string serviceId) => new(null, serviceId);
}
