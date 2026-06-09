// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// A plugin extension point for observing and gating edit batches. <see cref="OnBeforeEditAsync"/>
/// runs after per-feature validation and before the write, and may reject the whole batch.
/// <see cref="OnAfterEditAsync"/> runs after a successful write so plugins can react to committed
/// changes (e.g. derived bookkeeping, external notifications).
/// </summary>
/// <remarks>
/// Implementations are resolved as singletons from the DI container and must be thread-safe.
/// Hooks run in deterministic plugin-id order. A before-hook rejection short-circuits the
/// remaining before-hooks. After-hooks are best-effort: an exception thrown by one after-hook
/// must not roll back an already-committed edit and does not prevent other after-hooks running.
/// </remarks>
public interface IEditHook
{
    /// <summary>
    /// Invoked before an edit batch is written. May reject the entire batch.
    /// </summary>
    /// <param name="context">The batch of features about to be written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="EditHookResult"/> allowing or rejecting the batch.</returns>
    ValueTask<EditHookResult> OnBeforeEditAsync(EditHookContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Invoked after an edit batch has been committed successfully.
    /// </summary>
    /// <param name="context">The batch of features that were written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask OnAfterEditAsync(EditHookContext context, CancellationToken cancellationToken);
}
