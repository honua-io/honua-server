// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Admin observability: consolidated ops-health snapshot + deterministic ops-findings engine
    // (ADR-0060 WS4 / epic #2457). Expression-bodied (computed) to stay independent of cross-file
    // static-init order, mirroring the other EndpointRegistry.<Area> fragments.
    private static IReadOnlyList<EndpointDefinition> AdminObservabilityEndpoints =>
    [
        new("GET", "/api/v1/admin/observability/ops-health"),
        new("GET", "/api/v1/admin/observability/ops-health/history"),
        new("GET", "/api/v1/admin/observability/findings"),
        new("POST", "/api/v1/admin/observability/findings/{findingId}/propose"),
    ];
}
