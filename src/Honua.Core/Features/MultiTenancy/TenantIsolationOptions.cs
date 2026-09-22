// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.MultiTenancy;

/// <summary>
/// Config-bound tenant-isolation facts a <c>Honua.Core</c> feature slice needs in order to
/// decide whether a durable record belongs to the request's resolved tenant.
/// </summary>
/// <remarks>
/// <para>
/// Binds from the same <c>MultiTenancy</c> configuration section that
/// <c>Honua.Infrastructure.MultiTenancy.TenantContextOptions</c> uses to resolve
/// <see cref="Abstractions.ITenantContext"/>. <c>Honua.Core</c> cannot reference
/// <c>Honua.Hosting</c>'s options type directly (dependency direction), so this type re-reads
/// the identical config key -- the same pattern
/// <see cref="Authorization.AdminRoleOptions"/> uses for <c>Oidc:AdminRoles</c>. For any given
/// deployment both types therefore resolve the same effective values.
/// </para>
/// <para>
/// Only the members a Core-level ownership check needs are mirrored; claim types, the override
/// header and the id-length bound stay with the resolution layer that owns them.
/// </para>
/// </remarks>
public sealed class TenantIsolationOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "MultiTenancy";

    /// <summary>
    /// Whether tenant context resolution runs. When <see langword="false"/> no tenant is ever
    /// resolved, so tenant ownership is not enforced and single-tenant behaviour is unchanged.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The tenant unauthenticated and tenant-less-but-authenticated requests fall back to.
    /// Records written before tenant stamping existed carry no tenant of their own and are
    /// therefore attributed to this tenant (see <see cref="TenantOwnership.ResolveOwnerTenant"/>),
    /// which is exactly the tenant such a deployment's requests resolve to.
    /// </summary>
    public string? DefaultTenantId { get; set; } = "public";

    /// <summary>
    /// Role names whose holders operate across every tenant (the same roles that may override
    /// the request tenant through the <c>X-Honua-Tenant</c> header). Tenant ownership is not
    /// enforced against them.
    /// </summary>
    public string[] MultiTenantAdminRoles { get; set; } = ["multi_tenant_admin", "platform_admin"];
}
