// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Tenant-scope visibility rules for Metadata v2 graph entities (#1580).
/// </summary>
/// <remarks>
/// <para>
/// An entity declares its owning tenant through
/// <see cref="MetadataV2ObjectMetadata.Tenant"/>. The rules are intentionally simple
/// and fail closed:
/// </para>
/// <list type="bullet">
///   <item><description>An entity with no tenant (<see langword="null"/> or
///   whitespace) is <b>unscoped</b> and visible to every request, including requests
///   that resolved no tenant context. This preserves single-tenant behavior for
///   existing graphs.</description></item>
///   <item><description>A tenant-scoped entity is visible only when the request
///   resolved a tenant id that matches exactly (ordinal, case-sensitive). A request
///   without a tenant context never sees scoped entities.</description></item>
///   <item><description>A publication is visible only when the publication itself,
///   its canonical resource, and the publishing service are all visible — scoping any
///   one node in the chain hides the published surface.</description></item>
/// </list>
/// <para>
/// Shared lookup helpers (layer/collection validation, STAC publication resolution)
/// apply these rules <em>before</em> access-policy evaluation and storage-layer
/// resolution, so cross-tenant requests observe "not found" rather than "forbidden"
/// and cannot probe for another tenant's identifiers.
/// </para>
/// </remarks>
public static class MetadataV2TenantVisibility
{
    /// <summary>
    /// Core rule: is an entity owned by <paramref name="entityTenant"/> visible to a
    /// request resolved to <paramref name="requestTenant"/>?
    /// </summary>
    /// <param name="entityTenant">The entity's owning tenant; null/whitespace means unscoped.</param>
    /// <param name="requestTenant">The request's resolved tenant id; null/whitespace means
    /// no tenant context was resolved.</param>
    /// <returns><see langword="true"/> when the entity is visible to the request.</returns>
    public static bool IsVisibleToTenant(string? entityTenant, string? requestTenant)
    {
        if (string.IsNullOrWhiteSpace(entityTenant))
        {
            // Unscoped entity: visible to everyone (single-tenant back-compat).
            return true;
        }

        if (string.IsNullOrWhiteSpace(requestTenant))
        {
            // Scoped entity but no tenant resolved for the request: fail closed.
            return false;
        }

        return string.Equals(entityTenant, requestTenant, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a resource is visible to the request tenant. A null resource is treated
    /// as visible so callers can keep their existing null-handling (missing resources
    /// already resolve to "not found").
    /// </summary>
    /// <param name="resource">The canonical resource, when resolved.</param>
    /// <param name="requestTenant">The request's resolved tenant id.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    public static bool IsVisibleToTenant(MetadataV2Resource? resource, string? requestTenant)
        => resource is null || IsVisibleToTenant(resource.Metadata.Tenant, requestTenant);

    /// <summary>
    /// Whether a service is visible to the request tenant. A null service is treated
    /// as visible (publications without a resolvable service are filtered elsewhere).
    /// </summary>
    /// <param name="service">The publishing service, when resolved.</param>
    /// <param name="requestTenant">The request's resolved tenant id.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    public static bool IsVisibleToTenant(MetadataV2Service? service, string? requestTenant)
        => service is null || IsVisibleToTenant(service.Metadata.Tenant, requestTenant);

    /// <summary>
    /// Combined rule for a published surface: the publication, its canonical resource,
    /// and the publishing service must all be visible to the request tenant.
    /// </summary>
    /// <param name="publication">The publication being resolved.</param>
    /// <param name="resource">The canonical resource backing the publication, when resolved.</param>
    /// <param name="service">The service the publication is exposed through, when resolved.</param>
    /// <param name="requestTenant">The request's resolved tenant id.</param>
    /// <returns><see langword="true"/> when the published surface is visible.</returns>
    public static bool IsVisibleToTenant(
        MetadataV2Publication publication,
        MetadataV2Resource? resource,
        MetadataV2Service? service,
        string? requestTenant)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return IsVisibleToTenant(publication.Metadata.Tenant, requestTenant)
            && IsVisibleToTenant(resource, requestTenant)
            && IsVisibleToTenant(service, requestTenant);
    }
}
