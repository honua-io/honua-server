// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> MapServerEndpoints =>
    [
        new("GET", "/rest/services/{serviceId}/MapServer"),
        new("POST", "/rest/services/{serviceId}/MapServer"),
        new("GET", "/rest/services/{serviceId}/MapServer/{layerId}"),
        new("POST", "/rest/services/{serviceId}/MapServer/{layerId}"),
        new("GET", "/rest/services/{serviceId}/MapServer/export"),
        new("POST", "/rest/services/{serviceId}/MapServer/export"),
        new("GET", "/rest/services/{serviceId}/MapServer/estimateExportTilesSize"),
        new("POST", "/rest/services/{serviceId}/MapServer/estimateExportTilesSize"),
        new("GET", "/rest/services/{serviceId}/MapServer/exportTiles"),
        new("POST", "/rest/services/{serviceId}/MapServer/exportTiles"),
        new("GET", "/rest/services/{serviceId}/MapServer/jobs/{jobId}"),
        new("POST", "/rest/services/{serviceId}/MapServer/jobs/{jobId}/cancel"),
        new("GET", "/rest/services/{serviceId}/MapServer/jobs/{jobId}/results/out_service_url"),
        new("GET", "/rest/services/{serviceId}/MapServer/generateKml"),
        new("POST", "/rest/services/{serviceId}/MapServer/generateKml"),
        new("GET", "/rest/services/{serviceId}/MapServer/identify"),
        new("POST", "/rest/services/{serviceId}/MapServer/identify"),
        new("GET", "/rest/services/{serviceId}/MapServer/legend"),
        new("POST", "/rest/services/{serviceId}/MapServer/legend"),
        new("GET", "/rest/services/{serviceId}/MapServer/queryLegends"),
        new("POST", "/rest/services/{serviceId}/MapServer/queryLegends"),
        new("GET", "/rest/services/{serviceId}/MapServer/find"),
        new("POST", "/rest/services/{serviceId}/MapServer/find"),
        new("GET", "/rest/services/{serviceId}/MapServer/{layerId}/query"),
        new("POST", "/rest/services/{serviceId}/MapServer/{layerId}/query"),
        new("GET", "/rest/services/{serviceId}/MapServer/query"),
        new("POST", "/rest/services/{serviceId}/MapServer/query"),
        new("GET", "/rest/services/{serviceId}/MapServer/allLayersAndTables"),
        new("GET", "/rest/services/{serviceId}/MapServer/layers"),
        new("GET", "/rest/services/{serviceId}/MapServer/queryDomains"),
        // queryDomains also accepts POST so clients can submit large layers arrays (#1825).
        new("POST", "/rest/services/{serviceId}/MapServer/queryDomains"),
        new("GET", "/rest/services/{serviceId}/MapServer/dynamicLayer"),
        new("GET", "/rest/services/{serviceId}/MapServer/generateRenderer"),
        new("POST", "/rest/services/{serviceId}/MapServer/generateRenderer"),
        new("GET", "/rest/services/{serviceId}/MapServer/{layerId}/generateRenderer"),
        new("POST", "/rest/services/{serviceId}/MapServer/{layerId}/generateRenderer"),
        new("GET", "/rest/services/{serviceId}/MapServer/{layerId}/queryRelatedRecords"),
        new("POST", "/rest/services/{serviceId}/MapServer/{layerId}/queryRelatedRecords"),
        new("GET", "/rest/services/{serviceId}/MapServer/{layerId}/queryAttachments"),
        new("POST", "/rest/services/{serviceId}/MapServer/{layerId}/queryAttachments"),
        new("GET", "/rest/services/{serviceId}/MapServer/{layerId}/{featureId}"),
        new("GET", "/rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}"),
        new("GET", "/rest/services/{serviceId}/MapServer/WMTS"),
        new("GET", "/rest/services/{serviceId}/MapServer/WMTS/{**restPath}"),
        new("GET", "/rest/services/{serviceId}/MapServer/WMS"),
        new("GET", "/ogc/services/{serviceId}/wmts"),
        new("GET", "/ogc/services/{serviceId}/wms"),
        new("GET", "/ogc/services/{serviceId}/wcs"),
    ];
}
