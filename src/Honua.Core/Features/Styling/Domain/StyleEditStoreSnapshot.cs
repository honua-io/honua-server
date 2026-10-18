// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

/// <summary>One coherent canonical read of style bytes, optional layer mirror and affected associations.</summary>
public sealed record StyleEditStoreSnapshot(string StyleId, StyleCatalogRecord? CatalogStyle,
    LayerStyleDefinition? LayerStyle, IReadOnlyList<StyleLayerAssociation> Associations)
{
    /// <summary>Server-derived canonical Studio consumers, never caller-declared dependency claims.</summary>
    public IReadOnlyList<StylePackageConsumer> Consumers { get; init; } = [];
    /// <summary>False when the derived index is unavailable, incomplete, or exceeds its bounded read.</summary>
    public bool ConsumerIndexComplete { get; init; }
    /// <summary>Actual canonical aliases whose unresolved references load this style.</summary>
    public IReadOnlyList<string> ReferenceIds { get; init; } = [];
}

/// <summary>Private canonical consumer snapshot. Owner/tenant are authorization inputs, not public impact fields.</summary>
public sealed record StylePackageConsumer(string Kind, Guid ItemId, string PackageKey,
    Guid? VersionId, Guid? DraftId, long? Generation, string ContentHash,
    string? OwnerActorId, string? TenantId, bool IsCurrent, bool IsPublished, string? WorkspaceId);
