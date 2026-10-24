// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
    /// the invocation is accepted, or immediately when the bounded queue is full
    /// and the invocation is dropped. The handler runs out of band. This method
    /// does not wait for queue capacity.
    /// </summary>
    ValueTask EnqueueAsync(
        FieldCollectionActionInvocation invocation,
        CancellationToken cancellationToken = default);
}
