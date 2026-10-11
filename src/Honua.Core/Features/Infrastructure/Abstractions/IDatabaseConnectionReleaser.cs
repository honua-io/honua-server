// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Closes the idle physical database connections a provider keeps pooled in this process.
/// <para>
/// A long-lived host keeps its pools warm, so it never calls this. An AWS Lambda instance is
/// frozen between invocations and nothing (pool pruning included) runs while it is frozen, so
/// every frozen instance would keep holding its pooled connections open against the database.
/// The control-plane event handlers (reconcile, backstop, scheduled tick) therefore release at
/// the end of every invocation; see <c>ControlPlane:Lambda:ReleaseConnectionsAfterInvocation</c>.
/// </para>
/// </summary>
public interface IDatabaseConnectionReleaser
{
    /// <summary>
    /// Closes every idle pooled connection now; connections that are in use are closed when
    /// they are returned to the pool instead of being reused. The pools stay usable: the next
    /// open simply establishes a new physical connection.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the pools have been cleared.</returns>
    ValueTask ReleasePooledConnectionsAsync(CancellationToken cancellationToken = default);
}
