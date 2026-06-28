// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> OgcApiEndpoints =>
    [
        new("GET", "/ogc/features"),
        new("GET", "/ogc/features/conformance"),
        new("GET", "/openapi.json"),
        new("GET", "/ogc/features/collections"),
        new("GET", "/ogc/features/collections/{collectionId}"),
        new("GET", "/ogc/features/collections/{collectionId}/queryables"),
        new("GET", "/ogc/features/collections/{collectionId}/items"),
        new("GET", "/ogc/features/api"),
        new("GET", "/ogc/features/schemas/honua-ogcapi-features.xsd"),
        new("GET", "/ogc/features/collections/{collectionId}/items/{featureId}"),
        new("POST", "/ogc/features/collections/{collectionId}/items"),
        new("POST", "/ogc/features/collections/{collectionId}/items/batch"),
        new("PUT", "/ogc/features/collections/{collectionId}/items/{featureId}"),
        new("PATCH", "/ogc/features/collections/{collectionId}/items/{featureId}"),
        new("DELETE", "/ogc/features/collections/{collectionId}/items/{featureId}"),
        new("GET", "/ogc/features/collections/{collectionId}/h3"),
        new("POST", "/ogc/features/collections/{collectionId}/clusters"),
        new("POST", "/ogc/features/collections/{collectionId}/spatial-join"),
        new("POST", "/ogc/features/collections/{collectionId}/buffer-aggregate"),
        new("POST", "/ogc/features/collections/{collectionId}/density"),

        // OGC API Records
        new("GET", "/ogc/records"),
        new("GET", "/ogc/records/conformance"),
        new("GET", "/ogc/records/collections"),
        new("GET", "/ogc/records/collections/{collectionId}"),
        new("GET", "/ogc/records/collections/{collectionId}/items"),
        new("GET", "/ogc/records/collections/{collectionId}/items/{recordId}"),

        // OGC API Coverages
        new("GET", "/ogc/coverages"),
        new("GET", "/ogc/coverages/conformance"),
        new("GET", "/ogc/coverages/api"),
        new("GET", "/ogc/coverages/openapi.json"),
        new("GET", "/ogc/coverages/collections"),
        new("GET", "/ogc/coverages/collections/{collectionId}"),
        new("GET", "/ogc/coverages/collections/{collectionId}/schema"),
        new("GET", "/ogc/coverages/collections/{collectionId}/coverage"),

        // OGC API - Environmental Data Retrieval (EDR) (#1757)
        new("GET", "/edr"),
        new("GET", "/edr/conformance"),
        new("GET", "/edr/collections"),
        new("GET", "/edr/collections/{collectionId}"),
        new("GET", "/edr/collections/{collectionId}/position"),
        new("GET", "/edr/collections/{collectionId}/cube"),

        // OGC API Processes
        new("GET", "/ogc/processes"),
        new("GET", "/ogc/processes/conformance"),
        new("GET", "/ogc/processes/openapi.json"),
        new("GET", "/ogc/processes/processes"),
        new("GET", "/ogc/processes/processes/{processId}"),
        new("POST", "/ogc/processes/processes/{processId}/execution"),
        new("GET", "/ogc/processes/jobs"),
        new("GET", "/ogc/processes/jobs/{jobId}"),
        new("GET", "/ogc/processes/jobs/{jobId}/results"),
        new("DELETE", "/ogc/processes/jobs/{jobId}"),

        new("GET", "/ogc/tiles"),
        new("GET", "/ogc/tiles/conformance"),
        new("GET", "/ogc/tiles/openapi.json"),
        new("GET", "/ogc/tiles/collections"),
        new("GET", "/ogc/tiles/collections/{collectionId}"),
        new("GET", "/ogc/tiles/collections/{collectionId}/tiles"),
        new("GET", "/ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}"),
        new("GET", "/ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}"),
        new("GET", "/ogc/tiles/tiles"),
        new("GET", "/ogc/tiles/tiles/{tileMatrixSetId}"),
        new("GET", "/ogc/tiles/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}"),
        new("GET", "/ogc/tiles/tileMatrixSets"),
        new("GET", "/ogc/tiles/tileMatrixSets/{tileMatrixSetId}"),

    ];
}
