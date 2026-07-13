// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Mobile.FieldCollection.Domain;

/// <summary>
/// Pure matching logic that decides which configured automation actions fire for
/// an applied FieldCollection change. Kept free of I/O so the trigger decision is
/// deterministic and exhaustively unit-testable.
/// </summary>
public static class FieldCollectionAutomationMatcher
{
    /// <summary>
    /// Returns whether <paramref name="action"/> should run for
    /// <paramref name="automationEvent"/>. An action matches when it is enabled,
    /// its layer scope is unset or equal to the event's layer, and its operation
    /// filter is empty or contains the event's operation.
    /// </summary>
    public static bool Matches(
        FieldCollectionAutomationAction action,
        FieldCollectionAutomationEvent automationEvent)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(automationEvent);

        if (!action.Enabled)
        {
            return false;
        }

        if (action.LayerId is int layerId && layerId != automationEvent.LayerId)
        {
            return false;
        }

        if (!action.Operations.IsDefaultOrEmpty
            && !action.Operations.Contains(automationEvent.Operation))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Materializes the invocations that should be dispatched for
    /// <paramref name="automationEvent"/> from the supplied action definitions,
    /// preserving the order the actions were provided in.
    /// </summary>
    public static IReadOnlyList<FieldCollectionActionInvocation> Match(
        IEnumerable<FieldCollectionAutomationAction> actions,
        FieldCollectionAutomationEvent automationEvent)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(automationEvent);

        var matched = actions
            .Where(action => Matches(action, automationEvent))
            .Select(action => FieldCollectionActionInvocation.Create(action, automationEvent))
            .ToList();

        return matched;
    }
}
