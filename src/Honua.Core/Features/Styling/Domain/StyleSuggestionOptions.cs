// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Styling.Domain;

/// <summary>
/// Caller-supplied overrides for style suggestion behavior.
/// </summary>
public sealed class StyleSuggestionOptions
{
    /// <summary>
    /// Override: use this field for classification instead of auto-selection.
    /// </summary>
    public string? PreferredField { get; init; }

    /// <summary>
    /// Override: use this classification method.
    /// </summary>
    public ClassificationMethod? PreferredMethod { get; init; }

    /// <summary>
    /// Override: use this palette name.
    /// </summary>
    public string? PreferredPalette { get; init; }

    /// <summary>
    /// Number of classes to generate (default 5).
    /// </summary>
    public int ClassCount { get; init; } = 5;

    /// <summary>
    /// Caller's license edition. Propagated to the <see cref="StyleSuggestion.Edition"/>
    /// result so the domain model reflects the actual tier rather than a hard-coded value.
    /// </summary>
    public HonuaEdition Edition { get; init; } = HonuaEdition.Community;
}
