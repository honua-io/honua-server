// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Federation.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Federation.Abstractions;

/// <summary>
/// Executes a federated query: it plans the push-down, fetches candidate rows from the remote
/// source through a <see cref="IFederatedSourceConnector"/> under a per-source timeout and
/// circuit breaker, then refines and combines the rows locally. This is the remote-execution
/// layer that sits on top of the offline <see cref="IFederationQueryPlanner"/> (issue #341).
/// </summary>
public interface IFederatedQueryExecutor
{
    /// <summary>
    /// Executes a federated query against a single source.
    /// </summary>
    /// <param name="source">The target federated source.</param>
    /// <param name="query">The canonical query to execute.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The combined remote + locally refined result for the source.</returns>
    /// <exception cref="FederatedSourceUnavailableException">
    /// Thrown when the source times out, faults, has an open circuit, or has no registered connector.
    /// </exception>
    Task<FederatedQueryResult> ExecuteAsync(
        FederatedSourceDescriptor source,
        FeatureQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a federated query across multiple sources and merges the results. A source that
    /// is unavailable is recorded as a failure rather than failing the whole request, so a
    /// single failing source cannot cascade into a total outage.
    /// </summary>
    /// <param name="sources">The target federated sources. Disabled sources are skipped.</param>
    /// <param name="query">The canonical query to execute against each source.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The merged result across all reachable sources, plus any per-source failures.</returns>
    Task<FederatedCombinedResult> ExecuteAsync(
        ImmutableArray<FederatedSourceDescriptor> sources,
        FeatureQuery query,
        CancellationToken cancellationToken = default);
}
