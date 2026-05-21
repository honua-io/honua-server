// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;

namespace Honua.Server.Features.Infrastructure.MultiTenancy;

/// <summary>
/// Per-request implementation of <see cref="ITenantContext"/>. Populated by
/// <c>TenantContextMiddleware</c>; resolved from the request scope by handlers.
/// </summary>
internal sealed class RequestTenantContext : ITenantContext
{
    /// <inheritdoc />
    public string? TenantId { get; private set; }

    /// <inheritdoc />
    public TenantContextSource Source { get; private set; } = TenantContextSource.Anonymous;

    /// <summary>
    /// Assigns the resolved tenant id and source. Intended to be called exactly once per
    /// request by <c>TenantContextMiddleware</c>; subsequent calls overwrite the value
    /// (useful only for tests).
    /// </summary>
    /// <param name="tenantId">The resolved tenant identifier, or <see langword="null"/> if none.</param>
    /// <param name="source">The resolution source.</param>
    public void Set(string? tenantId, TenantContextSource source)
    {
        TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
        Source = source;
    }

    /// <inheritdoc />
    public bool RequireTenantId(out string tenantId, out string? reason)
    {
        if (!string.IsNullOrWhiteSpace(TenantId))
        {
            tenantId = TenantId!;
            reason = null;
            return true;
        }

        tenantId = string.Empty;
        reason = Source switch
        {
            TenantContextSource.Anonymous => "anonymous request and no default tenant configured",
            TenantContextSource.Claim => "tenant claim missing from principal",
            TenantContextSource.Header => "tenant header missing or rejected",
            TenantContextSource.Default => "default tenant not configured",
            _ => "no tenant context resolved",
        };
        return false;
    }
}
