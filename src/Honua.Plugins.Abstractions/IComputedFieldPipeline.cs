// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Plugins.Abstractions;

/// <summary>
/// Host-facing orchestrator that applies registered plugin <see cref="IComputedFieldProvider"/>s
/// at the query projection seam. A single implementation is resolved from DI; when no Enterprise
/// license / no computed-field providers are present the host registers
/// <see cref="NoOpComputedFieldPipeline"/> so read paths have zero overhead.
/// </summary>
/// <remarks>
/// This is the only computed-field type a protocol assembly needs to consume — it depends solely
/// on this abstraction package and never on plugin internals. Apply it after a feature has been
/// read and before it is serialized to the protocol response.
/// </remarks>
public interface IComputedFieldPipeline
{
    /// <summary>
    /// Gets whether any computed-field providers are active. Hosts can skip projection entirely
    /// when this is <see langword="false"/>.
    /// </summary>
    bool HasComputedFields { get; }

    /// <summary>
    /// Projects all registered computed fields onto a single feature, returning a feature whose
    /// attribute bag includes the derived values. Providers that throw are isolated and skipped.
    /// </summary>
    /// <param name="feature">The feature read from storage.</param>
    /// <param name="context">Layer/service identity for the query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The feature with computed fields added; the input unchanged when nothing applies.</returns>
    ValueTask<Feature> ProjectAsync(
        Feature feature,
        ComputedFieldContext context,
        CancellationToken cancellationToken);
}
