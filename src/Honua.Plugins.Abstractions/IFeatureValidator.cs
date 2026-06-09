// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Plugins.Abstractions;

/// <summary>
/// A plugin extension point for custom, business-rule feature validation. Implementations
/// are invoked for each feature being created or updated, after the platform's built-in
/// attribute/geometry validation and before the edit is written. Returning an error rejects
/// the feature.
/// </summary>
/// <remarks>
/// Implementations are resolved as singletons from the DI container and must be thread-safe.
/// Validators run in deterministic plugin-id order; the first error for a given feature
/// short-circuits the remaining validators for that feature.
/// </remarks>
public interface IFeatureValidator
{
    /// <summary>
    /// Validates a single feature participating in an edit.
    /// </summary>
    /// <param name="feature">The resolved feature (WKB geometry + merged attributes) about to be written.</param>
    /// <param name="context">Layer/service identity and edit kind for the feature.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="PluginValidationResult"/> indicating success or rejection.</returns>
    ValueTask<PluginValidationResult> ValidateAsync(
        Feature feature,
        PluginValidationContext context,
        CancellationToken cancellationToken);
}
