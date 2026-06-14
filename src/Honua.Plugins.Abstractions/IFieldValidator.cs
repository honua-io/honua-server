// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Plugins.Abstractions;

/// <summary>
/// A plugin extension point for custom, per-attribute validation. Unlike
/// <see cref="IFeatureValidator"/> (which sees the whole feature), a field validator declares
/// the single attribute it governs via <see cref="FieldName"/> and is only invoked when that
/// attribute is present on a feature being created or updated. This keeps simple per-column
/// business rules small and composable without each plugin re-walking the attribute bag.
/// </summary>
/// <remarks>
/// Implementations are resolved as singletons from the DI container and must be thread-safe.
/// Field validators run after the platform's built-in attribute/geometry validation and after
/// feature-level <see cref="IFeatureValidator"/>s, in deterministic plugin-id order. The first
/// error for a given feature short-circuits the remaining field validators for that feature.
/// </remarks>
public interface IFieldValidator
{
    /// <summary>
    /// Gets the case-insensitive attribute name this validator governs. The validator is only
    /// invoked for features whose attribute bag contains a key equal to this value.
    /// </summary>
    string FieldName { get; }

    /// <summary>
    /// Validates the governed attribute's value for a single feature participating in an edit.
    /// </summary>
    /// <param name="value">The attribute value (may be <see langword="null"/>).</param>
    /// <param name="feature">The full feature, for rules that depend on sibling attributes.</param>
    /// <param name="context">Layer/service identity and edit kind for the feature.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="PluginValidationResult"/> indicating success or rejection.</returns>
    ValueTask<PluginValidationResult> ValidateFieldAsync(
        object? value,
        Feature feature,
        PluginValidationContext context,
        CancellationToken cancellationToken);
}
