// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Plugins.Abstractions;

/// <summary>
/// A plugin extension point for query-time derived/virtual fields. A provider declares the
/// attribute name it contributes via <see cref="FieldName"/> and computes that attribute's value
/// from the queried feature at the projection seam — the value is added to the feature's
/// attribute bag in the response without being persisted. This mirrors the simple-op precedent in
/// <c>ComputedFieldTransformExecutor</c> (Honua.Geoprocessing) but as a compile-time, AOT-safe
/// plugin contract.
/// </summary>
/// <remarks>
/// Implementations are resolved as singletons from the DI container and must be thread-safe and
/// side-effect-free: computed fields run on read paths and must not mutate state. Providers run
/// in deterministic plugin-id order; a later provider that declares the same field name as an
/// earlier one wins. Computing a value is best-effort — a provider that throws is isolated and
/// its field is simply omitted, never failing the query.
/// </remarks>
public interface IComputedFieldProvider
{
    /// <summary>
    /// Gets the attribute name this provider contributes to query responses. The value is added
    /// under this key in the projected feature's attribute bag.
    /// </summary>
    string FieldName { get; }

    /// <summary>
    /// Computes the derived value for a single queried feature.
    /// </summary>
    /// <param name="feature">The feature being projected (geometry + persisted attributes).</param>
    /// <param name="context">Layer/service identity for the query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The computed value to add under <see cref="FieldName"/>; may be <see langword="null"/>.</returns>
    ValueTask<object?> ComputeAsync(
        Feature feature,
        ComputedFieldContext context,
        CancellationToken cancellationToken);
}
