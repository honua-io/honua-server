// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Federation.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Federation.Abstractions;

/// <summary>
/// Plans how a canonical <see cref="FeatureQuery"/> is split across a federated source and
/// the local federation layer: which predicates push down to the remote source and which
/// are refined locally. Planning is pure and deterministic so it can run offline, ahead of
/// any remote call, and be unit-tested without a live remote warehouse.
/// </summary>
public interface IFederationQueryPlanner
{
    /// <summary>
    /// Resolves the offline push-down capabilities for a federated source kind.
    /// </summary>
    /// <param name="kind">The transport family of the remote source.</param>
    /// <returns>The capabilities used to drive push-down decisions.</returns>
    FederatedSourceCapabilities ResolveCapabilities(FederatedSourceKind kind);

    /// <summary>
    /// Builds the federation plan for a query against a single source.
    /// </summary>
    /// <param name="source">The target federated source descriptor.</param>
    /// <param name="query">The canonical query to plan.</param>
    /// <param name="joinsLocalLayer">
    /// <see langword="true"/> when the query combines the remote source with a local
    /// PostGIS layer, which forces a local join step.
    /// </param>
    /// <returns>The push-down / local-refinement plan.</returns>
    FederationQueryPlan Plan(FederatedSourceDescriptor source, in FeatureQuery query, bool joinsLocalLayer);
}
