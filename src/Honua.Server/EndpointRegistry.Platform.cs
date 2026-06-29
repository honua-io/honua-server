// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> PlatformEndpoints =>
    [
        new("GET", "/healthz/live"),
        new("POST", "/healthz/live"),
        new("PUT", "/healthz/live"),
        new("DELETE", "/healthz/live"),
        new("PATCH", "/healthz/live"),
        new("GET", "/healthz/ready"),
        new("POST", "/healthz/ready"),
        new("PUT", "/healthz/ready"),
        new("DELETE", "/healthz/ready"),
        new("PATCH", "/healthz/ready"),
        new("GET", "/healthz/metrics"),
        new("POST", "/healthz/metrics"),
        new("PUT", "/healthz/metrics"),
        new("DELETE", "/healthz/metrics"),
        new("PATCH", "/healthz/metrics"),
        new("GET", "/metrics"),
        new("GET", "/monitoring/health/production"),
        new("GET", "/monitoring/metrics/connection-pool"),
        new("GET", "/monitoring/metrics/cache"),
        new("GET", "/monitoring/metrics/resources"),
        new("GET", "/monitoring/alerts"),
        new("GET", "/monitoring/metrics/upload-queue"),
        new("GET", "/monitoring/health/comprehensive"),
        new("GET", "/monitoring/metrics/database-resilience"),

        // Interactive API explorer (Scalar)
        new("GET", "/docs"),
    ];
}
