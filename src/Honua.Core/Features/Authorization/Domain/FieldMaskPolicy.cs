// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Field-level security (column masking) policy (#1940) — the field-level companion to
/// row-level security (<see cref="RlsPolicy"/>, #502). Attaches a single masked attribute
/// (column) to a service/layer, keyed by the role it applies to. At query time the masked
/// attribute is dropped from the projected attributes server-side so a user whose role is
/// restricted never receives the column's value — regardless of the <c>outFields</c> /
/// <c>$select</c> / <c>properties</c> the client requested.
/// </summary>
/// <remarks>
/// <para>The policy is intentionally <em>structured</em> (role + scope + attribute) rather
/// than free-form SQL. The masked attribute is removed from the attributes JSON via the
/// PostgreSQL <c>jsonb</c> key-removal operator with the field name bound as a parameter,
/// so a policy can never introduce SQL injection regardless of its contents.</para>
/// <para>Masking is fail-secure: it is applied at the shared projection seam inside the
/// feature store (parallel to the <c>EnforcedSqlFilter</c> chokepoint used for rows), so a
/// single enforcement point covers every query surface (GeoServices, OGC API Features,
/// OData) and every output format (Esri JSON, GeoJSON, GML, KML, raw fast paths).</para>
/// </remarks>
public sealed class FieldMaskPolicy
{
    /// <summary>
    /// Unique identifier for this policy.
    /// </summary>
    public Guid PolicyId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Role name this policy applies to (case-insensitive), or "*" for every role.
    /// A request masks the union of attributes from all policies matching its roles.
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Service name this policy applies to, or "*" for all services.
    /// </summary>
    public required string Service { get; init; }

    /// <summary>
    /// Layer identifier this policy applies to, or "*" for all layers.
    /// </summary>
    public required string Layer { get; init; }

    /// <summary>
    /// The layer attribute (field name) that is masked (dropped) from query output
    /// for the matching role (e.g. "ssn").
    /// </summary>
    public required string Attribute { get; init; }

    /// <summary>
    /// Optional human-readable description of the policy's intent.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// When the policy was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the policy was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
