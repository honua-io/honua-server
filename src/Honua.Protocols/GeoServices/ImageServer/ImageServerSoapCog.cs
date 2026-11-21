// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Services;
using static Honua.Protocols.GeoServices.Soap.GeoServicesSoapProtocol;

namespace Honua.Protocols.GeoServices.ImageServer;

// SOAP operations on a service whose imagery is only registered cloud COGs, described from the COG
// grid as the REST service document is. ExportImage composes the COG tiles through exportImage.
internal static partial class ImageServerSoapEndpoints
{
    private const int CogServiceHistogramBins = 256;

    private const string CogEntitlementFault = "COG Serving requires an active Pro entitlement.";

    // The COG grid of a layer holding no raster_data rows, with the layer it was resolved for; no
    // rasters when the layer has no scanned COG, and a fault where its tiles would answer 402.
    private static async Task<(RasterInfo[] Rasters, ImageServerCogLayer? Layer, IResult? Error)> ReadCogServiceRastersAsync(
        SoapRasterRequestContext request, CancellationToken cancellationToken)
    {
        var graphProvider = request.HttpContext.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        // The publication the resolver validated, not whichever publication shares its storage layer.
        if (request.Resolution.PublicationId is not { } publicationId
            || ImageServerV2Lookups.FindByPublicationId(snapshot, publicationId, request.HttpContext) is not { } resolved)
        {
            return ([], null, null);
        }

        var layer = new ImageServerCogLayer(snapshot, resolved.Publication, []);
        var cogs = await ImageServerCogRasters.ResolveAsync(request.HttpContext, layer, cancellationToken).ConfigureAwait(false);
        if (cogs.Refusal is { } refusal)
        {
            return ([], layer, CreateSoapFaultFromResult(refusal, CogEntitlementFault, request.SoapNamespace));
        }

        return (cogs.Rasters, cogs.Rasters.Length > 0 ? layer : null, null);
    }

    // The layer's raster_data rasters, else its COG grid when its imagery is only registered COGs.
    private static async Task<(RasterInfo[] Rasters, ImageServerCogLayer? Layer, IResult? Error)> ReadRastersOrCogsAsync(
        SoapRasterRequestContext request, IRasterStore rasterStore, CancellationToken cancellationToken)
    {
        var layerId = request.Resolution.LayerId;
        var rasters = await rasterStore.ListRastersAsync(layerId, cancellationToken).ConfigureAwait(false);
        return rasters.Length > 0
            ? (rasters, null, null)
            : await ReadCogServiceRastersAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // GetServiceInfo statistics and histograms of a COG-only service from its COGs' coarsest
    // overviews. A COG the statistics reader cannot read leaves them empty rather than failing the
    // service description, as raster_data services without cached statistics do.
    private static async Task<(RasterStatistics[] Statistics, ServiceInfoExtras Extras)> ReadCogServiceInfoAsync(
        SoapRasterRequestContext request, ImageServerCogLayer layer, CancellationToken cancellationToken)
    {
        var cogs = await ImageServerCogStatistics.TryComputeAsync(
            request.HttpContext, new ImageServerCogStatisticsRequest(layer, null, CogServiceHistogramBins), cancellationToken).ConfigureAwait(false);
        var computed = cogs is { Refusal: null } ? cogs : new ImageServerCogStatisticsResult([], [], null);
        XNamespace xsi = XmlSchemaInstanceNamespace;
        var histograms = new XElement(
            "Histograms",
            new XAttribute(xsi + "type", "tns:ArrayOfRasterHistogram"),
            computed.Histograms.Select(ImageServerHistogramProjection.Project).Select(BuildRasterHistogram));
        return (computed.Statistics, new ServiceInfoExtras(false, null, null, null, histograms, null));
    }

    // GetImage returns Esri pixel blocks, which only raster_data rasters supply.
    private static async Task<IResult> CreateNoRastersFaultAsync(
        SoapRasterRequestContext request, CancellationToken cancellationToken)
    {
        var cogs = await ReadCogServiceRastersAsync(request, cancellationToken).ConfigureAwait(false);
        return cogs.Rasters.Length > 0
            ? CreateSoapFault(
                "SOAP GetImage returns pixel blocks, which cloud COG layers do not supply; use ExportImage.",
                StatusCodes.Status501NotImplemented,
                request.SoapNamespace)
            : cogs.Error ?? CreateSoapFault("Image service has no rasters.", StatusCodes.Status404NotFound, request.SoapNamespace);
    }
}
