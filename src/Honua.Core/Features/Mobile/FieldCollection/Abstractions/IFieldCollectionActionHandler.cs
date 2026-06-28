// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Mobile.FieldCollection.Domain;

namespace Honua.Core.Features.Mobile.FieldCollection.Abstractions;

/// <summary>
/// Delivers a single online action for one automation kind (#2121). One handler
/// is registered per <see cref="FieldCollectionAutomationActionType"/>; the
/// background dispatcher routes invocations to the handler matching the action's
/// type.
/// </summary>
public interface IFieldCollectionActionHandler
{
    /// <summary>Gets the action kind this handler delivers.</summary>
    FieldCollectionAutomationActionType ActionType { get; }

    /// <summary>
    /// Delivers <paramref name="invocation"/>. Implementations must not throw for
    /// expected delivery failures; they should return a non-successful
    /// <see cref="FieldCollectionActionResult"/> instead so the dispatcher can apply
    /// retry policy.
    /// </summary>
    Task<FieldCollectionActionResult> ExecuteAsync(
        FieldCollectionActionInvocation invocation,
        CancellationToken cancellationToken = default);
}
