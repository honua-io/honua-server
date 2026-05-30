// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Middleware;

internal static partial class TenantContextLog
{
    [LoggerMessage(
        EventId = 7001,
        Level = LogLevel.Debug,
        Message = "Tenant context resolved from {Source} as '{TenantId}' for path '{Path}'.")]
    public static partial void Resolved(ILogger logger, string source, string tenantId, string? path);

    [LoggerMessage(
        EventId = 7002,
        Level = LogLevel.Warning,
        Message = "Cross-tenant header override accepted: principal '{Principal}' set tenant to '{TenantId}' via header for path '{Path}'.")]
    public static partial void HeaderOverrideAccepted(ILogger logger, string? principal, string tenantId, string? path);

    [LoggerMessage(
        EventId = 7003,
        Level = LogLevel.Warning,
        Message = "Cross-tenant header override rejected: principal '{Principal}' lacks multi-tenant admin role; ignoring '{Header}' header for path '{Path}'.")]
    public static partial void HeaderOverrideRejected(ILogger logger, string? principal, string header, string? path);

    [LoggerMessage(
        EventId = 7004,
        Level = LogLevel.Debug,
        Message = "No tenant context resolved for anonymous request to path '{Path}' (no default configured).")]
    public static partial void AnonymousNoDefault(ILogger logger, string? path);

    [LoggerMessage(
        EventId = 7005,
        Level = LogLevel.Warning,
        Message = "Rejected tenant value of length {Length} from {Source} for path '{Path}'; exceeds MaxTenantIdLength.")]
    public static partial void RejectedOversizedTenantValue(ILogger logger, int length, string source, string? path);
}
