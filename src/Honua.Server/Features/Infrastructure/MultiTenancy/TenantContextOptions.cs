// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.MultiTenancy;

/// <summary>
/// Configuration for tenant context resolution (issue #1144).
/// </summary>
public sealed class TenantContextOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "MultiTenancy";

    /// <summary>
    /// HTTP header used to override the tenant id for callers that hold a
    /// multi-tenant admin role. Reads of this header are logged for audit.
    /// </summary>
    public const string TenantHeaderName = "X-Honua-Tenant";

    /// <summary>
    /// Gets or sets whether tenant context resolution is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the claim types inspected (in order) to discover the tenant id on the
    /// authenticated principal. The defaults match Azure AD (<c>tid</c>) and the generic
    /// <c>tenant_id</c> convention used by most OIDC providers.
    /// </summary>
    public string[] TenantClaimTypes { get; set; } = ["tid", "tenant_id"];

    /// <summary>
    /// Gets or sets the role names that authorize a principal to override the tenant
    /// via the <see cref="TenantHeaderName"/> header. Empty array disables header overrides.
    /// </summary>
    public string[] MultiTenantAdminRoles { get; set; } = ["multi_tenant_admin", "platform_admin"];

    /// <summary>
    /// Gets or sets the default tenant id used for unauthenticated public reads
    /// (anonymous OGC / STAC clients). When <see langword="null"/> or empty, anonymous
    /// requests will have a null <see cref="Honua.Core.Features.MultiTenancy.Abstractions.ITenantContext.TenantId"/>.
    /// </summary>
    public string? DefaultTenantId { get; set; } = "public";

    /// <summary>
    /// Gets or sets the maximum accepted length for a tenant id sourced from either a
    /// claim or the override header. Provides a defensive bound against log-injection
    /// and pathological values.
    /// </summary>
    public int MaxTenantIdLength { get; set; } = 128;
}
