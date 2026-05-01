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

    /// <summary>ArcGIS service rejected the scan with an access-denied response.</summary>
    public const string ArcGisAccessDenied = "ARCGIS_ACCESS_DENIED";

    /// <summary>ArcGIS service returned a generic non-auth error response.</summary>
    public const string ArcGisServiceError = "ARCGIS_SERVICE_ERROR";

    /// <summary>ArcGIS coded-value domain exceeded the deterministic capture cap.</summary>
    public const string ArcGisDomainTruncated = "ARCGIS_DOMAIN_TRUNCATED";
}
