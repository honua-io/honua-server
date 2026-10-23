// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Relationship declaration captured from an Esri layer or table. A declaration
/// is source evidence, not proof that its target binding or ownership behavior exists.
/// Missing source facts remain nullable rather than being inferred from object IDs.
/// </summary>
public sealed record GeoservicesRelationshipInfo
{
    /// <summary>Source service-local relationship identifier.</summary>
    public int? Id { get; init; }

    /// <summary>Source relationship name.</summary>
    public string? Name { get; init; }

    /// <summary>Related layer/table identifier in the same source service.</summary>
    public int? RelatedTableId { get; init; }

    /// <summary>Source key field on this side of the relationship.</summary>
    public string? KeyField { get; init; }

    /// <summary>Source origin or destination role, without normalization.</summary>
    public string? Role { get; init; }

    /// <summary>Source cardinality, without inventing a supported target mapping.</summary>
    public string? Cardinality { get; init; }

    /// <summary>Whether the source declares composite ownership semantics.</summary>
    public bool? Composite { get; init; }
}
