// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> AdminAlertEndpoints =>
    [
        // v1 gitops watch endpoints removed in #1035 cutover.
        new("GET", "/api/v1/admin/alerts/zones"),
        new("GET", "/api/v1/admin/alerts/zones/{zoneId}"),
        new("POST", "/api/v1/admin/alerts/zones"),
        new("PUT", "/api/v1/admin/alerts/zones/{zoneId}"),
        new("DELETE", "/api/v1/admin/alerts/zones/{zoneId}"),
        new("GET", "/api/v1/admin/alerts/rules"),
        new("GET", "/api/v1/admin/alerts/rules/{ruleId}"),
        new("POST", "/api/v1/admin/alerts/rules"),
        new("POST", "/api/v1/admin/alerts/rules/test"),
        new("PUT", "/api/v1/admin/alerts/rules/{ruleId}"),
        new("PUT", "/api/v1/admin/alerts/rules/{ruleId}/enabled"),
        new("GET", "/api/v1/admin/alerts/rules/{ruleId}/health"),
        new("GET", "/api/v1/admin/alerts/rules/{ruleId}/events"),
        new("DELETE", "/api/v1/admin/alerts/rules/{ruleId}"),
        // Alert dispatch self-healing ops actuators (#2561).
        new("POST", "/api/v1/admin/alerts/dispatch/redrive"),
        new("GET", "/api/v1/admin/alerts/channels"),
        new("POST", "/api/v1/admin/alerts/channels/{channel}/pause"),
        new("POST", "/api/v1/admin/alerts/channels/{channel}/resume"),
    ];
}
