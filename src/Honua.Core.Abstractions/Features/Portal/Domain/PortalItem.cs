// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Portal.Domain;

/// <summary>
/// An ArcGIS Portal / Sharing REST item document projected from a Metadata v2
/// service/resource. The shape matches what the Esri Portal
/// <c>content/items/{id}</c> and <c>search</c> endpoints return so packaged Esri
/// clients (ArcGIS Pro "Add Portal", Field Maps) can discover and open Honua
/// content as portal items.
/// </summary>
/// <remarks>
/// This is the wire-neutral projection produced by <c>IPortalItemProjector</c>.
/// The Portal HTTP read endpoints (#1243) consume this directly; they own the
/// <c>info</c>/<c>portals/self</c>/<c>search</c>/<c>content/items/{id}</c> route
/// surface and any response envelope wrapping (search result paging, etc.).
/// </remarks>
public sealed record PortalItem
{
    /// <summary>
    /// Stable Portal item identifier. Derived from the Metadata v2 service id so it
    /// is stable across requests and across the search / item-detail endpoints.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Owner username of the item. Derived from the service publisher / owner; falls
    /// back to the deployment's portal owner when unset.
    /// </summary>
    [JsonPropertyName("owner")]
    public required string Owner { get; init; }

    /// <summary>
    /// Epoch-millisecond creation timestamp.
    /// </summary>
    [JsonPropertyName("created")]
    public long Created { get; init; }

    /// <summary>
    /// Epoch-millisecond last-modified timestamp.
    /// </summary>
    [JsonPropertyName("modified")]
    public long Modified { get; init; }

    /// <summary>
    /// Esri item type, e.g. <c>Feature Service</c>, <c>Map Service</c>, or
    /// <c>Image Service</c>.
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Esri item type keywords used by clients to refine capability detection.
    /// </summary>
    [JsonPropertyName("typeKeywords")]
    public IReadOnlyList<string> TypeKeywords { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Human-readable item title.
    /// </summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>
    /// Item snippet (short description).
    /// </summary>
    [JsonPropertyName("snippet")]
    public string? Snippet { get; init; }

    /// <summary>
    /// Item long-form description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Discovery tags.
    /// </summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The service URL the item resolves to, pointing at the existing
    /// <c>/rest/services/...</c> endpoint. Honours the forwarded scheme/host so
    /// links remain correct behind a reverse proxy.
    /// </summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>
    /// Item access level — <c>public</c>, <c>org</c>, or <c>private</c>. A
    /// projection of the RBAC <c>AccessPolicy</c>: <c>public</c> when anonymous
    /// read is allowed; <c>org</c> when any authenticated principal is allowed;
    /// <c>private</c> when access is role-restricted.
    /// </summary>
    [JsonPropertyName("access")]
    public required string Access { get; init; }

    /// <summary>
    /// Item extent as <c>[[xmin, ymin], [xmax, ymax]]</c> in WGS84, or an empty
    /// array when the resource declares no extent. Matches the Esri item-extent
    /// shape.
    /// </summary>
    [JsonPropertyName("extent")]
    public IReadOnlyList<IReadOnlyList<double>> Extent { get; init; } =
        Array.Empty<IReadOnlyList<double>>();

    /// <summary>
    /// Spatial reference well-known id (WKID) the item's extent is expressed in.
    /// </summary>
    [JsonPropertyName("spatialReference")]
    public string? SpatialReference { get; init; }

    /// <summary>
    /// Culture / language tag for the item.
    /// </summary>
    [JsonPropertyName("culture")]
    public string? Culture { get; init; }

    /// <summary>
    /// Accumulated number of comments (always 0 — comments are out of scope per
    /// #1243). Present for client-shape fidelity.
    /// </summary>
    [JsonPropertyName("numComments")]
    public int NumComments { get; init; }

    /// <summary>
    /// Number of views (always 0 — view tracking is out of scope). Present for
    /// client-shape fidelity.
    /// </summary>
    [JsonPropertyName("numViews")]
    public int NumViews { get; init; }
}

/// <summary>
/// Portal access tiers projected from the RBAC <c>AccessPolicy</c>.
/// </summary>
public static class PortalAccessLevels
{
    /// <summary>Anonymous read is permitted.</summary>
    public const string Public = "public";

    /// <summary>Any authenticated principal is permitted, but not anonymous.</summary>
    public const string Org = "org";

    /// <summary>Access is restricted to specific roles.</summary>
    public const string Private = "private";
}

/// <summary>
/// Esri Portal item type strings used by the projector.
/// </summary>
public static class PortalItemTypes
{
    /// <summary>Feature Service item type.</summary>
    public const string FeatureService = "Feature Service";

    /// <summary>Map Service item type.</summary>
    public const string MapService = "Map Service";

    /// <summary>Image Service item type.</summary>
    public const string ImageService = "Image Service";
}
