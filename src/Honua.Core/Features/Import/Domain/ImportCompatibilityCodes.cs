// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Stable machine-readable compatibility codes emitted by migration inventory
/// scanners. Codes are namespaced per source kind so downstream automation can
/// branch deterministically without parsing free-text reasons.
/// </summary>
public static class ImportCompatibilityCodes
{
    /// <summary>Scanned item is fully compatible.</summary>
    public const string Compatible = "COMPATIBLE";

    /// <summary>Scanned item is partially compatible and needs follow-up review.</summary>
    public const string ManualReview = "MANUAL_REVIEW";

    /// <summary>Aggregate placeholder for empty inventory aggregates.</summary>
    public const string Empty = "EMPTY";

    /// <summary>ArcGIS renderer type cannot be portably translated.</summary>
    public const string ArcGisUnsupportedRenderer = "ARCGIS_UNSUPPORTED_RENDERER";

    /// <summary>ArcGIS renderer mixes supported and unsupported renderer types.</summary>
    public const string ArcGisMixedRenderers = "ARCGIS_MIXED_RENDERERS";

    /// <summary>ArcGIS renderer references one or more external symbol URLs.</summary>
    public const string ArcGisExternalSymbol = "ARCGIS_EXTERNAL_SYMBOL";

    /// <summary>ArcGIS resource lacks spatial reference metadata.</summary>
    public const string ArcGisMissingSpatialRef = "ARCGIS_MISSING_SPATIAL_REF";

    /// <summary>ArcGIS resource advertises a geometry type that the import path does not support.</summary>
    public const string ArcGisUnsupportedGeometry = "ARCGIS_UNSUPPORTED_GEOMETRY";

    /// <summary>ArcGIS resource lacks query capability.</summary>
    public const string ArcGisQueryCapabilityMissing = "ARCGIS_QUERY_CAPABILITY_MISSING";

    /// <summary>ArcGIS resource advertises attachments requiring a separate migration.</summary>
    public const string ArcGisAttachments = "ARCGIS_ATTACHMENTS";

    /// <summary>ArcGIS service requires a token before scanning may proceed.</summary>
    public const string ArcGisTokenRequired = "ARCGIS_TOKEN_REQUIRED";

    /// <summary>ArcGIS service rejected the supplied token as invalid or expired.</summary>
    public const string ArcGisTokenExpired = "ARCGIS_TOKEN_EXPIRED";

    /// <summary>ArcGIS service rejected the scan with an access-denied response.</summary>
    public const string ArcGisAccessDenied = "ARCGIS_ACCESS_DENIED";

    /// <summary>ArcGIS service returned a generic non-auth error response.</summary>
    public const string ArcGisServiceError = "ARCGIS_SERVICE_ERROR";

    /// <summary>ArcGIS coded-value domain exceeded the deterministic capture cap.</summary>
    public const string ArcGisDomainTruncated = "ARCGIS_DOMAIN_TRUNCATED";

    /// <summary>ArcGIS subtype metadata is captured but not automatically migrated by this slice.</summary>
    public const string ArcGisSubtypesManualReview = "ARCGIS_SUBTYPES_MANUAL_REVIEW";

    /// <summary>ArcGIS relationship metadata is captured but not automatically migrated by this slice.</summary>
    public const string ArcGisRelationshipsManualReview = "ARCGIS_RELATIONSHIPS_MANUAL_REVIEW";

    /// <summary>ArcGIS time metadata is captured but not automatically migrated by this slice.</summary>
    public const string ArcGisTimeMetadataManualReview = "ARCGIS_TIME_METADATA_MANUAL_REVIEW";

    /// <summary>GeoServer resource or endpoint is supported by the scanner's migration path.</summary>
    public const string GeoServerSupported = "GEOSERVER_SUPPORTED";

    /// <summary>GeoServer resource needs manual review but can remain in the inventory plan.</summary>
    public const string GeoServerManualReview = "GEOSERVER_MANUAL_REVIEW";

    /// <summary>GeoServer datastore type is not supported by the automated migration path.</summary>
    public const string GeoServerUnsupportedStore = "GEOSERVER_UNSUPPORTED_STORE";

    /// <summary>GeoServer coverage store type is not supported by the automated migration path.</summary>
    public const string GeoServerUnsupportedCoverageStore = "GEOSERVER_UNSUPPORTED_COVERAGE_STORE";

    /// <summary>GeoServer layer is disabled and requires operator confirmation before enabling in the target.</summary>
    public const string GeoServerDisabledLayer = "GEOSERVER_DISABLED_LAYER";

    /// <summary>GeoServer layer group is empty and needs manual review.</summary>
    public const string GeoServerEmptyLayerGroup = "GEOSERVER_EMPTY_LAYER_GROUP";

    /// <summary>GeoServer SLD style requires style conversion work before automated migration.</summary>
    public const string GeoServerStyleConversionRequired = "GEOSERVER_STYLE_CONVERSION_REQUIRED";

    /// <summary>GeoServer style format is not supported by the automated migration path.</summary>
    public const string GeoServerUnsupportedStyleFormat = "GEOSERVER_UNSUPPORTED_STYLE_FORMAT";

    /// <summary>GeoServer style references an external graphic or URL dependency.</summary>
    public const string GeoServerExternalGraphic = "GEOSERVER_EXTERNAL_GRAPHIC";

    /// <summary>GeoServer advertised service endpoint was captured for downstream migration planning.</summary>
    public const string GeoServerServiceEndpoint = "GEOSERVER_SERVICE_ENDPOINT";

    /// <summary>GeoServer inventory scan could not complete.</summary>
    public const string GeoServerScanFailed = "GEOSERVER_SCAN_FAILED";

    /// <summary>OGC WFS feature type metadata is supported by the migration inventory path.</summary>
    public const string OgcWfsFeatureSource = "OGC_WFS_FEATURE_SOURCE";

    /// <summary>OGC feature schema metadata was unavailable or incomplete.</summary>
    public const string OgcFeatureSchemaManualReview = "OGC_FEATURE_SCHEMA_MANUAL_REVIEW";

    /// <summary>OGC WMS exposes rendered maps but not an automated feature data-copy source.</summary>
    public const string OgcWmsRenderOnlySource = "OGC_WMS_RENDER_ONLY_SOURCE";

    /// <summary>OGC WMTS exposes rendered tiles but not an automated feature data-copy source.</summary>
    public const string OgcWmtsTileOnlySource = "OGC_WMTS_TILE_ONLY_SOURCE";

    /// <summary>OGC service inventory scan could not complete.</summary>
    public const string OgcScanFailed = "OGC_SCAN_FAILED";

    /// <summary>WMS layer metadata (name, abstract, bounds, srs, keywords) projects deterministically.</summary>
    public const string OgcWmsMetadataAutomated = "OGC_WMS_METADATA_AUTOMATED";

    /// <summary>WMTS layer metadata projects deterministically.</summary>
    public const string OgcWmtsMetadataAutomated = "OGC_WMTS_METADATA_AUTOMATED";

    /// <summary>WMS/WMTS SLD/SE style references were captured for assisted import in a later slice.</summary>
    public const string OgcRenderStyleAssisted = "OGC_RENDER_STYLE_ASSISTED";

    /// <summary>WMTS tile matrix set is a trivial XYZ/TMS-like grid and can be projected automatically.</summary>
    public const string OgcWmtsTileMatrixAutomated = "OGC_WMTS_TILE_MATRIX_AUTOMATED";

    /// <summary>WMTS tile matrix set is non-trivial and needs operator review before mapping to a Honua cache.</summary>
    public const string OgcWmtsTileMatrixManualReview = "OGC_WMTS_TILE_MATRIX_MANUAL_REVIEW";

    /// <summary>WMS render-only source cannot supply automated feature data migration in this slice.</summary>
    public const string OgcWmsRenderDataUnsupported = "OGC_WMS_RENDER_DATA_UNSUPPORTED";

    /// <summary>WMTS tile-only source cannot supply automated feature data migration in this slice.</summary>
    public const string OgcWmtsTileDataUnsupported = "OGC_WMTS_TILE_DATA_UNSUPPORTED";

    /// <summary>WMS/WMTS render or tile endpoint captured for downstream service migration planning.</summary>
    public const string OgcRenderEndpointPlanned = "OGC_RENDER_ENDPOINT_PLANNED";
}
