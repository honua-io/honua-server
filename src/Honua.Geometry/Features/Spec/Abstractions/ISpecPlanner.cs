// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

/// <summary>
/// Produces a <see cref="SpecPlan"/> from a <see cref="CanonicalSpecDocument"/>.
/// </summary>
/// <remarks>
/// Planning is side-effect-free: it reads only the catalog and metadata stores.
/// It does not invoke the compute backend or mutate any state.
/// </remarks>
public interface ISpecPlanner
{
    /// <summary>
    /// Resolves the DAG shape, assigns content hashes, gathers per-node cost
    /// estimates, and returns a <see cref="SpecPlan"/>.
    /// </summary>
    /// <param name="document">Canonical spec document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A fully populated <see cref="SpecPlan"/>.</returns>
    Task<SpecPlan> PlanAsync(CanonicalSpecDocument document, CancellationToken cancellationToken = default);
}
