// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> AdminOperationEndpoints =>
    [
        // v1 admin operational monitoring endpoints (#512)
        new("GET", "/api/v1/admin/operations/cache/health"),
        new("GET", "/api/v1/admin/operations/cache/statistics"),
        new("GET", "/api/v1/admin/operations/cache/redis"),
        new("POST", "/api/v1/admin/operations/cache/invalidate"),
        new("GET", "/api/v1/admin/operations/streaming/subscribers"),
        new("GET", "/api/v1/admin/operations/streaming/alerts"),
        new("DELETE", "/api/v1/admin/operations/streaming/subscribers/{subscriberId}"),

        // v1 feature-change streaming endpoints (#501)
        new("GET", "/api/v1/streaming/features"),
        new("GET", "/api/v1/streaming/features/capabilities"),
        new("GET", "/api/v1/admin/streaming/features/sessions"),
        new("DELETE", "/api/v1/admin/streaming/features/sessions/{sessionId}"),

        new("GET", "/api/v1/admin/operations/geocoding/providers"),
        new("GET", "/api/v1/admin/operations/geocoding/configuration"),

        // v1 admin operations progress endpoints
        new("GET", "/api/v1/admin/operations/{operationId}"),
        new("POST", "/api/v1/admin/operations/{operationId}/cancel"),
        new("GET", "/api/v1/admin/operations/active"),
        new("GET", "/api/v1/admin/operations/type/{operationType}"),
        new("GET", "/api/v1/admin/feature-events/replay"),

        // Console durable job observability endpoints (#1170)
        new("GET", "/api/v1/admin/jobs"),
        new("GET", "/api/v1/admin/jobs/{jobId}"),
        new("GET", "/api/v1/admin/jobs/{jobId}/logs"),
        new("GET", "/api/v1/admin/jobs/{jobId}/artifacts"),
        new("GET", "/api/v1/admin/jobs/{jobId}/actions"),
        new("GET", "/api/v1/admin/jobs/{jobId}/steps"),
        new("POST", "/api/v1/admin/jobs/{jobId}/cancel"),
        new("POST", "/api/v1/admin/jobs/{jobId}/retry"),

        // Console Share export jobs and traffic endpoints (#1216)
        new("GET", "/api/v1/admin/share/exports"),
        new("POST", "/api/v1/admin/share/exports"),
        new("GET", "/api/v1/admin/share/exports/{exportId}"),
        new("PUT", "/api/v1/admin/share/exports/{exportId}"),
        new("DELETE", "/api/v1/admin/share/exports/{exportId}"),
        new("POST", "/api/v1/admin/share/exports/{exportId}/trigger"),
        new("POST", "/api/v1/admin/share/exports/{exportId}/pause"),
        new("POST", "/api/v1/admin/share/exports/{exportId}/resume"),
        new("GET", "/api/v1/admin/share/exports/{exportId}/runs"),
        new("GET", "/api/v1/admin/share/exports/{exportId}/runs/{runId}"),
        new("GET", "/api/v1/admin/share/traffic"),
        new("GET", "/api/v1/admin/share/traffic/series"),
        new("GET", "/api/v1/admin/services/{serviceName}/layers/{layerId}/share/traffic"),
        new("GET", "/api/v1/admin/services/{serviceName}/layers/{layerId}/share/traffic/series"),

        // Mobile runtime auth and diagnostics endpoints (#924)
        new("POST", "/api/mobile/exceptions"),

        // FieldCollection mobile sync endpoints (#894)
        new("GET", "/api/v1/fieldcollection/generation"),
        new("GET", "/api/v1/fieldcollection/sync-cursor"),
        new("POST", "/api/v1/fieldcollection/sync-cursor"),
        new("GET", "/api/v1/fieldcollection/changes"),
        new("POST", "/api/v1/fieldcollection/changes"),

        // Forms package lifecycle, offline policy, and submissions (#1184)
        new("GET", "/api/v1/admin/forms/packages"),
        new("POST", "/api/v1/admin/forms/packages"),
        new("POST", "/api/v1/admin/forms/packages/generate"),
        new("GET", "/api/v1/admin/forms/packages/{formId}"),
        new("GET", "/api/v1/admin/forms/packages/{formId}/versions"),
        new("GET", "/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}"),
        new("PUT", "/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}"),
        new("POST", "/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/validate"),
        new("POST", "/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/publish"),
        new("POST", "/api/v1/admin/forms/packages/{formId}/versions/{packageVersion}/reopen"),
        // NL-assisted form package generation (#1184).
        new("POST", "/api/v1/admin/forms/packages/generate"),
        new("GET", "/api/v1/forms/packages/{formId}"),
        new("GET", "/api/v1/forms/packages/{formId}/versions/{packageVersion}"),
        new("GET", "/api/v1/forms/packages/{formId}/offline-policy"),
        new("GET", "/api/v1/forms/packages/{formId}/compatibility"),
        new("POST", "/api/v1/forms/packages/{formId}/submissions"),

        // Back-office field data review & QA (#1159)
        new("GET", "/api/v1/admin/field-workflows/submissions"),
        new("GET", "/api/v1/admin/field-workflows/submissions/{submissionId}"),
        new("POST", "/api/v1/admin/field-workflows/submissions/{submissionId}/assignment"),
        new("POST", "/api/v1/admin/field-workflows/submissions/{submissionId}/decision"),
        new("POST", "/api/v1/admin/field-workflows/submissions/{submissionId}/comments"),

        // Back-office field export packages (#1160)
        new("POST", "/api/v1/admin/field-workflows/exports"),
        new("GET", "/api/v1/admin/field-workflows/exports"),

        new("POST", "/api/v1/admin/tile-operations/jobs"),
        new("GET", "/api/v1/admin/tile-operations/jobs/{jobId}"),
        new("GET", "/api/v1/admin/tile-operations/jobs"),
        new("POST", "/api/v1/admin/tile-operations/jobs/{jobId}/cancel"),
        new("POST", "/api/v1/admin/tile-operations/jobs/{jobId}/retry"),
        new("POST", "/api/v1/admin/tile-operations/evict"),
        new("GET", "/api/v1/admin/tile-operations/cache/inventory"),

        new("GET", "/api/v1/admin/observability/errors"),
        new("GET", "/api/v1/admin/observability/telemetry"),
        new("GET", "/api/v1/admin/observability/migrations"),
        // Console Operate event viewer (#1168) — alerts, audit, unified events, logs, investigations.
        new("GET", "/api/v1/admin/observability/alerts"),
        new("GET", "/api/v1/admin/observability/alerts/{eventId}"),
        new("POST", "/api/v1/admin/observability/alerts/{eventId}/acknowledge"),
        new("POST", "/api/v1/admin/observability/alerts/{eventId}/suppress"),
        new("POST", "/api/v1/admin/observability/alerts/{eventId}/resolve"),
        new("GET", "/api/v1/admin/observability/audit"),
        new("GET", "/api/v1/admin/observability/audit/export"),
        new("GET", "/api/v1/admin/observability/audit/verify"),
        new("GET", "/api/v1/admin/observability/events"),
        new("GET", "/api/v1/admin/observability/logs"),
        new("GET", "/api/v1/admin/investigations"),
        new("POST", "/api/v1/admin/investigations"),
        new("GET", "/api/v1/admin/investigations/{investigationId}"),
        new("PATCH", "/api/v1/admin/investigations/{investigationId}"),
        new("POST", "/api/v1/admin/investigations/{investigationId}/pins"),
        new("DELETE", "/api/v1/admin/investigations/{investigationId}/pins/{pinId}"),
        new("POST", "/api/v1/admin/investigations/{investigationId}/links"),
        new("DELETE", "/api/v1/admin/investigations/{investigationId}/links/{linkId}"),
        new("POST", "/api/v1/admin/dev/fixtures/operate-observability/{profile}"),

        new("GET", "/api/v1/admin/performance/database/query-cache/statistics"),
        new("GET", "/api/v1/admin/performance/enhanced/database/query-performance"),
        new("GET", "/api/v1/admin/performance/enhanced/database/slow-queries"),
        new("GET", "/api/v1/admin/performance/enhanced/resources/tracking"),
        new("GET", "/api/v1/admin/performance/enhanced/resources/potential-leaks"),
        new("POST", "/api/v1/admin/performance/enhanced/resources/scan-leaks"),
        new("GET", "/api/v1/admin/performance/enhanced/exceptions/statistics"),
        new("GET", "/api/v1/admin/performance/enhanced/exceptions/recent"),
        new("GET", "/api/v1/admin/performance/enhanced/cache/statistics"),
        new("GET", "/api/v1/admin/performance/enhanced/cache/effectiveness"),
        new("DELETE", "/api/v1/admin/performance/enhanced/cache/invalidate"),
        new("GET", "/api/v1/admin/performance/enhanced/summary"),

        new("GET", "/api/v1/metrics/health"),
        new("GET", "/api/v1/metrics/performance"),
        new("GET", "/api/v1/metrics/database"),
        new("GET", "/api/v1/metrics/cache"),
        new("GET", "/api/v1/metrics/memory"),
        new("GET", "/api/v1/metrics/streaming"),

        new("POST", "/csp-violation-report"),
    ];
}
