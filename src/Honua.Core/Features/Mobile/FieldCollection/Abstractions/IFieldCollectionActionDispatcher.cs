// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Mobile.FieldCollection.Domain;

namespace Honua.Core.Features.Mobile.FieldCollection.Abstractions;

/// <summary>
/// Hand-off boundary that enqueues a matched action invocation for asynchronous
/// delivery (#2121). Enqueue must be fast and non-blocking so the mobile push
/// response is never delayed by online action delivery.
/// </summary>
public interface IFieldCollectionActionDispatcher
{
    /// <summary>
    /// Enqueues <paramref name="invocation"/> for background delivery. Returns once
    /// the invocation is accepted; the actual handler runs out of band.
    /// </summary>
    ValueTask EnqueueAsync(
        FieldCollectionActionInvocation invocation,
        CancellationToken cancellationToken = default);
}
