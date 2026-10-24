// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    private static IReadOnlyList<EndpointDefinition> GeoprocessingEndpoints =>
    [
        new("GET", "/api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content"),
        new("PUT", "/api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}"),
        new("GET", "/api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}"),
    ];
}
