// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    private static IReadOnlyList<EndpointDefinition> GeoprocessingEndpoints =>
    [
        new("GET", "/api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content"),
    ];
}
