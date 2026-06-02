// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Portal.Domain;

namespace Honua.Core.Features.Portal.Abstractions;

/// <summary>
/// Projects the canonical Metadata v2 graph into ArcGIS Portal / Sharing REST
/// item documents (<see cref="PortalItem"/>). This is the single, RBAC-aware
/// projection seam consumed by the Portal read endpoints (#1243) so the
/// service → item mapping, item URL emission, and the <c>access</c> decision
/// never diverge across <c>search</c> and <c>content/items/{id}</c>.
/// </summary>
/// <remarks>
/// <para>
/// The projector is wire-neutral: it takes a resolved <see cref="ClaimsPrincipal"/>
/// and an already-resolved public base URL (callers derive this from the shared
/// forwarded-scheme/host helper so item URLs are correct behind a proxy), and
/// returns plain <see cref="PortalItem"/> records. It does not touch
/// <c>HttpContext</c>, which keeps it testable in isolation and reusable from any
/// adapter.
/// </para>
/// <para>
/// RBAC visibility is enforced here, not by caller discipline: an item is only
/// returned when the supplied principal is permitted to read the underlying
/// service/resource, evaluated through the shared access-policy seam over the
/// resource/service <c>AccessPolicy</c>.
/// </para>
/// </remarks>
public interface IPortalItemProjector
{
    /// <summary>
    /// Projects every Esri-shaped service in the snapshot that the principal can
    /// read into a Portal item. Services the principal cannot access are omitted,
    /// which is exactly the filtering the Portal <c>search</c> endpoint needs.
    /// </summary>
    /// <param name="snapshot">The current Metadata v2 graph snapshot.</param>
    /// <param name="principal">The calling principal.</param>
    /// <param name="baseUrl">
    /// The public base URL (scheme + authority + optional path base) item URLs are
    /// built from. Callers resolve this via the shared forwarded-scheme/host helper.
    /// </param>
    /// <returns>The projected, access-filtered Portal items.</returns>
    IReadOnlyList<PortalItem> ProjectVisibleItems(
        MetadataV2GraphSnapshot snapshot,
        ClaimsPrincipal principal,
        string baseUrl);

    /// <summary>
    /// Projects a single Portal item by its item id (the Metadata v2 service id),
    /// honouring RBAC. Returns <see langword="null"/> when no Esri-shaped service
    /// matches the id <em>or</em> when the principal is not permitted to read it —
    /// the endpoint (#1243) maps both to the Esri-shaped 403/404 so the projector
    /// never leaks the existence of a hidden item.
    /// </summary>
    /// <param name="snapshot">The current Metadata v2 graph snapshot.</param>
    /// <param name="principal">The calling principal.</param>
    /// <param name="itemId">The requested Portal item id.</param>
    /// <param name="baseUrl">
    /// The public base URL item URLs are built from (see
    /// <see cref="ProjectVisibleItems"/>).
    /// </param>
    /// <returns>The projected item, or <see langword="null"/> when not visible.</returns>
    PortalItem? ProjectItem(
        MetadataV2GraphSnapshot snapshot,
        ClaimsPrincipal principal,
        string itemId,
        string baseUrl);
}
