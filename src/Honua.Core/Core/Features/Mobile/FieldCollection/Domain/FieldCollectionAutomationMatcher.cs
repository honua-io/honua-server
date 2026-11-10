// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Mobile.FieldCollection.Domain;

public static class FieldCollectionAutomationMatcher
{
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
