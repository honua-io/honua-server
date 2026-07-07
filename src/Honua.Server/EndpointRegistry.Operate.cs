// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Server-authoritative aggregated operational status (A12). One endpoint returns a server-computed
    // verdict plus per-domain rollups; guarded by the read-only ops-reader authorization policy.
    private static IReadOnlyList<EndpointDefinition> OperateStatusEndpoints =>
    [
        new("GET", "/api/v1/operate/status"),
    ];
}
