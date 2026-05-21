// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.MultiTenancy.Abstractions;

/// <summary>
/// Per-request tenant context resolved by the tenant middleware.
/// Handlers should call <see cref="RequireTenantId(out string, out string?)"/> before
/// executing tenant-scoped data access so cross-tenant enumeration is blocked.
/// </summary>
/// <remarks>
/// The tenant rail (issue #1144) splits resolution from enforcement:
///  - The <c>TenantContextMiddleware</c> populates this context from authenticated
///    claims, an opt-in admin header, or a configured default tenant.
///  - Feature handlers consume <see cref="ITenantContext"/> to filter queries.
/// Implementations are scoped per HTTP request and must not be cached across requests.
/// </remarks>
public interface ITenantContext
{
    /// <summary>
    /// Gets the resolved tenant identifier for the current request, or <see langword="null"/>
    /// if no tenant could be resolved (e.g. anonymous request with no configured default).
    /// </summary>
    string? TenantId { get; }

    /// <summary>
    /// Gets the resolution source that produced <see cref="TenantId"/>.
    /// </summary>
    TenantContextSource Source { get; }

    /// <summary>
    /// Convenience accessor for handlers that require a tenant id to execute.
    /// Returns <see langword="true"/> when <see cref="TenantId"/> is populated; otherwise
    /// returns <see langword="false"/> and surfaces a human-readable <paramref name="reason"/>
    /// suitable for inclusion in an error response (never leaks tenant identifiers).
    /// </summary>
    /// <param name="tenantId">When <see langword="true"/> is returned, the resolved tenant id.</param>
    /// <param name="reason">When <see langword="false"/> is returned, a short explanation
    /// such as "no tenant claim present" or "anonymous request and no default tenant configured".</param>
    /// <returns><see langword="true"/> if a tenant id is available; otherwise <see langword="false"/>.</returns>
    bool RequireTenantId(out string tenantId, out string? reason);
}

/// <summary>
/// Indicates how the current <see cref="ITenantContext.TenantId"/> was resolved.
/// </summary>
public enum TenantContextSource
{
    /// <summary>
    /// No tenant context has been resolved (e.g. anonymous request with no default).
    /// </summary>
    Anonymous = 0,

    /// <summary>
    /// Tenant id was extracted from a claim on the authenticated principal
    /// (for example the <c>tid</c> or <c>tenant_id</c> claim).
    /// </summary>
    Claim = 1,

    /// <summary>
    /// Tenant id was supplied by an explicit request header (<c>X-Honua-Tenant</c>)
    /// and accepted because the principal holds a multi-tenant admin role.
    /// </summary>
    Header = 2,

    /// <summary>
    /// Tenant id fell back to a configured default tenant (used for public OGC reads).
    /// </summary>
    Default = 3,
}
