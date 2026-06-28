// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Federation.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Federation.Abstractions;

/// <summary>
/// Transport boundary for a single federated source kind. A connector is the only piece of
/// the federation layer that performs remote I/O: it translates the pushed-down portion of a
/// <see cref="FederatedFetchRequest"/> into a remote request (Honua gRPC, Esri REST, OGC API
/// Features / WFS), executes it, and maps the response back to canonical
/// <see cref="Feature"/> rows. The federation executor wraps every call in a per-source
/// timeout and circuit breaker, so connectors should surface transport failures as exceptions
/// rather than swallowing them.
/// </summary>
public interface IFederatedSourceConnector
{
    /// <summary>
    /// Gets the transport family this connector services.
    /// </summary>
    FederatedSourceKind Kind { get; }

    /// <summary>
    /// Fetches the candidate rows for a federated fetch request. The connector applies the
    /// predicates the plan marked as pushed-down; the federation executor refines the
    /// remaining predicates locally.
    /// </summary>
    /// <param name="request">The fetch request, including the source, query, and plan.</param>
    /// <param name="cancellationToken">A token that is cancelled when the per-source timeout elapses.</param>
    /// <returns>The candidate rows fetched from the remote source.</returns>
    Task<ImmutableArray<Feature>> FetchAsync(FederatedFetchRequest request, CancellationToken cancellationToken);
}
