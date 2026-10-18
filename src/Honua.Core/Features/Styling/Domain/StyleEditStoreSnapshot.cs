// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

/// <summary>One coherent canonical read of style bytes, optional layer mirror and affected associations.</summary>
public sealed record StyleEditStoreSnapshot(string StyleId, StyleCatalogRecord? CatalogStyle,
    LayerStyleDefinition? LayerStyle, IReadOnlyList<StyleLayerAssociation> Associations);
