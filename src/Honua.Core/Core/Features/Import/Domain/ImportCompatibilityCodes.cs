// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

public static class ImportCompatibilityCodes
{
    public const string Compatible = "COMPATIBLE";

    public const string ManualReview = "MANUAL_REVIEW";

    public const string Empty = "EMPTY";

    public const string GeoServicesUnsupportedRenderer = "GEOSERVICES_UNSUPPORTED_RENDERER";

    public const string GeoServicesMixedRenderers = "GEOSERVICES_MIXED_RENDERERS";

    public const string GeoServicesExternalSymbol = "GEOSERVICES_EXTERNAL_SYMBOL";

    public const string GeoServicesMissingSpatialRef = "GEOSERVICES_MISSING_SPATIAL_REF";

    public const string GeoServicesUnsupportedGeometry = "GEOSERVICES_UNSUPPORTED_GEOMETRY";

    public const string GeoServicesQueryCapabilityMissing = "GEOSERVICES_QUERY_CAPABILITY_MISSING";

    public const string GeoServicesAttachments = "GEOSERVICES_ATTACHMENTS";

    public const string GeoServicesTokenRequired = "GEOSERVICES_TOKEN_REQUIRED";

    public const string GeoServicesTokenExpired = "GEOSERVICES_TOKEN_EXPIRED";

    public const string GeoServicesAccessDenied = "GEOSERVICES_ACCESS_DENIED";

    public const string GeoServicesServiceError = "GEOSERVICES_SERVICE_ERROR";

    public const string GeoServicesImportAborted = "GEOSERVICES_IMPORT_ABORTED";

    public const string GeoServicesImportGeometryRejected = "GEOSERVICES_IMPORT_GEOMETRY_REJECTED";

    public const string GeoServicesImportDatabaseError = "GEOSERVICES_IMPORT_DATABASE_ERROR";

    public const string GeoServicesImportDatabaseUnavailable = "GEOSERVICES_IMPORT_DATABASE_UNAVAILABLE";

    public const string GeoServicesImportFailed = "GEOSERVICES_IMPORT_FAILED";

    public const string GeoServicesDomainTruncated = "GEOSERVICES_DOMAIN_TRUNCATED";

    public const string GeoServicesSubtypesManualReview = "GEOSERVICES_SUBTYPES_MANUAL_REVIEW";

    public const string GeoServicesFeatureTypeTemplatesUnsupported = "GEOSERVICES_FEATURE_TYPE_TEMPLATES_UNSUPPORTED";

    public const string GeoServicesFeatureTypeDomainClearingUnsupported = "GEOSERVICES_FEATURE_TYPE_DOMAIN_CLEARING_UNSUPPORTED";

    public const string GeoServicesFeatureTypeIdentityUnsupported = "GEOSERVICES_FEATURE_TYPE_IDENTITY_UNSUPPORTED";

    public const string GeoServicesRelationshipsManualReview = "GEOSERVICES_RELATIONSHIPS_MANUAL_REVIEW";

    public const string GeoServicesTimeMetadataManualReview = "GEOSERVICES_TIME_METADATA_MANUAL_REVIEW";

    public const string GeoServerSupported = "GEOSERVER_SUPPORTED";

    public const string GeoServerManualReview = "GEOSERVER_MANUAL_REVIEW";

    public const string GeoServerUnsupportedStore = "GEOSERVER_UNSUPPORTED_STORE";

    public const string GeoServerUnsupportedCoverageStore = "GEOSERVER_UNSUPPORTED_COVERAGE_STORE";

    public const string GeoServerDisabledLayer = "GEOSERVER_DISABLED_LAYER";

    public const string GeoServerEmptyLayerGroup = "GEOSERVER_EMPTY_LAYER_GROUP";

    public const string GeoServerStyleConversionRequired = "GEOSERVER_STYLE_CONVERSION_REQUIRED";

    public const string GeoServerUnsupportedStyleFormat = "GEOSERVER_UNSUPPORTED_STYLE_FORMAT";

    public const string GeoServerExternalGraphic = "GEOSERVER_EXTERNAL_GRAPHIC";

    public const string GeoServerServiceEndpoint = "GEOSERVER_SERVICE_ENDPOINT";

    public const string GeoServerScanFailed = "GEOSERVER_SCAN_FAILED";

    public const string OgcWfsFeatureSource = "OGC_WFS_FEATURE_SOURCE";

    public const string OgcFeatureSchemaManualReview = "OGC_FEATURE_SCHEMA_MANUAL_REVIEW";

    public const string OgcWmsRenderOnlySource = "OGC_WMS_RENDER_ONLY_SOURCE";

    public const string OgcWmtsTileOnlySource = "OGC_WMTS_TILE_ONLY_SOURCE";

    public const string OgcScanFailed = "OGC_SCAN_FAILED";

    public const string OgcWmsMetadataAutomated = "OGC_WMS_METADATA_AUTOMATED";

    public const string OgcWmtsMetadataAutomated = "OGC_WMTS_METADATA_AUTOMATED";

    public const string OgcRenderStyleAssisted = "OGC_RENDER_STYLE_ASSISTED";

    public const string OgcWmtsTileMatrixAutomated = "OGC_WMTS_TILE_MATRIX_AUTOMATED";

    public const string OgcWmtsTileMatrixManualReview = "OGC_WMTS_TILE_MATRIX_MANUAL_REVIEW";

    public const string OgcWmsRenderDataUnsupported = "OGC_WMS_RENDER_DATA_UNSUPPORTED";

    public const string OgcWmtsTileDataUnsupported = "OGC_WMTS_TILE_DATA_UNSUPPORTED";

    public const string OgcRenderEndpointPlanned = "OGC_RENDER_ENDPOINT_PLANNED";

    public const string OgcWmsCompanionSourceHint = "OGC_WMS_COMPANION_SOURCE_HINT";

    public const string OgcWmtsTileCacheExported = "OGC_WMTS_TILE_CACHE_EXPORTED";

    public const string OgcWmtsTileCacheZoomThresholdExceeded = "OGC_WMTS_TILE_CACHE_ZOOM_THRESHOLD_EXCEEDED";

    public const string OgcWmtsTileCacheSkippedManualReview = "OGC_WMTS_TILE_CACHE_SKIPPED_MANUAL_REVIEW";

    public const string OgcWmtsTileCacheFetchFailed = "OGC_WMTS_TILE_CACHE_FETCH_FAILED";
}
