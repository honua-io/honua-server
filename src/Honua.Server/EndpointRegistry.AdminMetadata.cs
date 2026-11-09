// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> AdminMetadataLayerEndpoints =>
    [
        // v1 admin metadata resource CRUD endpoints removed in #1035 cutover.
        // (Layer-style/fields/filter/validation endpoints below still serve v1 layer ids.)
        new("GET", "/api/v1/admin/metadata/layers/{storageLayerId}/discovery"),
        new("PUT", "/api/v1/admin/metadata/layers/{storageLayerId}/discovery"),
        new("GET", "/api/v1/admin/services/{serviceName}/discovery"),
        new("PUT", "/api/v1/admin/services/{serviceName}/discovery"),
        new("GET", "/api/v1/admin/metadata/layers/{storageLayerId}/style"),
        new("PUT", "/api/v1/admin/metadata/layers/{storageLayerId}/style"),
        new("GET", "/api/v1/admin/metadata/layers/{storageLayerId}/fields"),
        new("PUT", "/api/v1/admin/metadata/layers/{storageLayerId}/fields"),
        new("GET", "/api/v1/admin/metadata/layers/{storageLayerId}/popup-info"),
        new("PUT", "/api/v1/admin/metadata/layers/{storageLayerId}/popup-info"),
        new("GET", "/api/v1/admin/metadata/layers/{storageLayerId}/drawing-info"),
        new("PUT", "/api/v1/admin/metadata/layers/{storageLayerId}/drawing-info"),
        new("GET", "/api/v1/admin/metadata/layers/{storageLayerId}/relationships"),
        new("PUT", "/api/v1/admin/metadata/layers/{storageLayerId}/relationships"),
        new("PUT", "/api/v1/admin/metadata/layers/relationships/batch"),
        new("GET", "/api/v1/admin/metadata/layers/{storageLayerId}/filter"),
        new("PUT", "/api/v1/admin/metadata/layers/{storageLayerId}/filter"),
        new("GET", "/api/v1/admin/metadata/layers/{storageLayerId}/validation"),
        new("POST", "/api/v1/admin/metadata/layers/{storageLayerId}/style/import-sld"),
        new("GET", "/api/v1/admin/metadata/layers/{storageLayerId}/style/export-sld"),
        new("POST", "/api/v1/admin/metadata/layers/{storageLayerId}/suggest-style"),
    ];
}
