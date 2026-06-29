// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> AdminMetadataLayerEndpoints =>
    [
        // v1 admin metadata resource CRUD endpoints removed in #1035 cutover.
        // (Layer-style/fields/filter/validation endpoints below still serve v1 layer ids.)
        new("GET", "/api/v1/admin/metadata/layers/{layerId}/style"),
        new("PUT", "/api/v1/admin/metadata/layers/{layerId}/style"),
        new("GET", "/api/v1/admin/metadata/layers/{layerId}/fields"),
        new("PUT", "/api/v1/admin/metadata/layers/{layerId}/fields"),
        new("GET", "/api/v1/admin/metadata/layers/{layerId}/popup-info"),
        new("PUT", "/api/v1/admin/metadata/layers/{layerId}/popup-info"),
        new("GET", "/api/v1/admin/metadata/layers/{layerId}/drawing-info"),
        new("PUT", "/api/v1/admin/metadata/layers/{layerId}/drawing-info"),
        new("GET", "/api/v1/admin/metadata/layers/{layerId}/relationships"),
        new("PUT", "/api/v1/admin/metadata/layers/{layerId}/relationships"),
        new("GET", "/api/v1/admin/metadata/layers/{layerId}/filter"),
        new("PUT", "/api/v1/admin/metadata/layers/{layerId}/filter"),
        new("GET", "/api/v1/admin/metadata/layers/{layerId}/validation"),
        new("POST", "/api/v1/admin/metadata/layers/{layerId}/style/import-sld"),
        new("GET", "/api/v1/admin/metadata/layers/{layerId}/style/export-sld"),
        new("POST", "/api/v1/admin/metadata/layers/{layerId}/suggest-style"),
    ];
}
