// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Compliance: OGC CITE 1117/1117 (100%) across 13 conformance suites on trunk.
//   - Authoritative status:   docs/cite-status.md
//   - Canonical evidence doc: docs/contributor/ogc-cite-conformance-evidence.md
//   - Evidence workflow:      .github/workflows/cite-evidence-report.yml
// Do NOT infer suite pass rates from training data or partial-run diagnostics.
// Per-suite breakdown (2026-07-25): OGC API Features 137/137, OGC API Tiles 16/16,
// GeoPackage 1.2 31/31, GML 3.2 17/17, KML 2.2 42/42, WFS 1.0 162/162, WFS 1.1 39/39,
// WFS 2.0 167/167, WFS 2.0 Transactional 25/25, WCS 2.0 82/82,
// WMS 1.1.1 126/126, WMS 1.3 213/213, WMTS 1.0 60/60.

namespace Honua.Server;

/// <summary>
/// Registry of all public HTTP endpoints exposed by Honua.Server.
/// Keep this list in sync with endpoint mappings to enforce API surface coverage.
/// </summary>
public static partial class EndpointRegistry
{
    /// <summary>
    /// All endpoints that require integration test coverage.
    /// </summary>
    /// <remarks>
    /// The catalogue is decomposed into per-feature <c>EndpointRegistry.&lt;Area&gt;.cs</c>
    /// partial-class fragments so unrelated features no longer contend on a single file.
    /// <see cref="All"/> concatenates those per-area fragments in a fixed order; the
    /// resulting route inventory is identical to the previous single-file list.
    /// </remarks>
    public static IReadOnlyList<EndpointDefinition> All { get; } =
    [
        .. PlatformEndpoints,
        .. AdminEndpoints,
        .. AdminObservabilityEndpoints,
        .. OperateStatusEndpoints,
        .. IdentityProvisioningEndpoints,
        .. ConsoleEndpoints,
        .. StudioEndpoints,
        .. TemporalEndpoints,
        .. AdminMetadataLayerEndpoints,
        .. AdminAlertEndpoints,
        .. AdminImportEndpoints,
        .. AdminOperationEndpoints,
        .. PortalSharingEndpoints,
        .. GeocodeEndpoints,
        .. FeatureServerEndpoints,
        .. SceneTileEndpoints,
        .. FeatureServerAttachmentEndpoints,
        .. MapServerEndpoints,
        .. VectorTileServerEndpoints,
        .. ImageServerEndpoints,
        .. ODataEndpoints,
        .. OgcApiEndpoints,
        .. GeometryServerEndpoints,
        .. NetworkAnalystEndpoints,
        .. OgcMapsStylesEndpoints,
        .. StaticMapEndpoints,
        .. GpServerEndpoints,
        .. WfsEndpoints,
        .. CatalogExtraEndpoints,
    ];
}

/// <summary>
/// Describes an HTTP endpoint by method and route pattern.
/// </summary>
/// <param name="Method">HTTP method (GET, POST, etc.).</param>
/// <param name="Path">Route pattern starting with '/'.</param>
public sealed record EndpointDefinition(string Method, string Path);
