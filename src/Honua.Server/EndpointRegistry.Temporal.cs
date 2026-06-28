// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> TemporalEndpoints =>
    [
        // Temporal data history (slice 1 of #1166): capability discovery + as-of read.
        new("GET", "/api/v1/temporal/services/{serviceId}/layers/{layerId}/capabilities"),
        new("GET", "/api/v1/temporal/services/{serviceId}/layers/{layerId}/as-of"),

        // Temporal data history (slices 2-5 of #1166): diff, feature timeline, governed rollback.
        new("GET", "/api/v1/temporal/services/{serviceId}/layers/{layerId}/diff"),
        new("GET", "/api/v1/temporal/services/{serviceId}/layers/{layerId}/features/{featureId}/timeline"),
        new("POST", "/api/v1/temporal/services/{serviceId}/layers/{layerId}/rollback/plan"),
        new("POST", "/api/v1/temporal/services/{serviceId}/layers/{layerId}/rollback"),

        new("GET", "/api/v1/published/{*routeSlug}"),

    ];
}
