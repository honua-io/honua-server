// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Routing.Features.Routing.Domain;

public static partial class NetworkTopologyLifecycle
{
    public static bool CanTransition(
        NetworkTopologyGenerationState current,
        NetworkTopologyGenerationState target) => (current, target) switch
        {
            (NetworkTopologyGenerationState.Draft, NetworkTopologyGenerationState.Dirty) => true,
            (NetworkTopologyGenerationState.Dirty, NetworkTopologyGenerationState.Building) => true,
            (NetworkTopologyGenerationState.Building, NetworkTopologyGenerationState.Ready) => true,
            (NetworkTopologyGenerationState.Building, NetworkTopologyGenerationState.Failed) => true,
            (NetworkTopologyGenerationState.Ready, NetworkTopologyGenerationState.Active) => true,
            (NetworkTopologyGenerationState.Ready, NetworkTopologyGenerationState.Failed) => true,
            (NetworkTopologyGenerationState.Active, NetworkTopologyGenerationState.Retired) => true,
            (NetworkTopologyGenerationState.Retired, NetworkTopologyGenerationState.Active) => true,
            (NetworkTopologyGenerationState.Failed, NetworkTopologyGenerationState.Dirty) => true,
            (NetworkTopologyGenerationState.Failed, NetworkTopologyGenerationState.Retired) => true,
            _ => false,
        };

    public static bool TryTransition(
        NetworkTopologyGeneration current,
        long expectedRowVersion,
        NetworkTopologyGenerationState target,
        DateTimeOffset occurredAt,
        string? failureCode,
        out NetworkTopologyGeneration updated,
        out NetworkTopologyTransitionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(current);

        updated = current;
        if (current.RowVersion != expectedRowVersion)
        {
            failure = NetworkTopologyTransitionFailure.StaleRowVersion;
            return false;
        }

        if (!CanTransition(current.State, target))
        {
            failure = NetworkTopologyTransitionFailure.InvalidTransition;
            return false;
        }

        if (target == NetworkTopologyGenerationState.Failed)
        {
            if (string.IsNullOrWhiteSpace(failureCode) || !StableFailureCodeRegex().IsMatch(failureCode))
            {
                failure = NetworkTopologyTransitionFailure.InvalidFailureCode;
                return false;
            }
        }
        else if (failureCode is not null)
        {
            failure = NetworkTopologyTransitionFailure.InvalidFailureCode;
            return false;
        }

        updated = current with
        {
            State = target,
            RowVersion = checked(current.RowVersion + 1),
            UpdatedAt = occurredAt,
            ActivatedAt = target == NetworkTopologyGenerationState.Active
                ? occurredAt
                : current.ActivatedAt,
            FailureCode = target == NetworkTopologyGenerationState.Failed ? failureCode : null,
        };
        failure = NetworkTopologyTransitionFailure.None;
        return true;
    }

    public static bool TryApplyContentEdit(
        NetworkTopologyGeneration current,
        long expectedRowVersion,
        DateTimeOffset occurredAt,
        out NetworkTopologyGeneration updated,
        out NetworkTopologyEditRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(current);

        updated = current;
        if (current.RowVersion != expectedRowVersion)
        {
            rejection = NetworkTopologyEditRejection.StaleRowVersion;
            return false;
        }

        if (current.State is not (NetworkTopologyGenerationState.Draft or NetworkTopologyGenerationState.Dirty))
        {
            rejection = NetworkTopologyEditRejection.GenerationNotEditable;
            return false;
        }

        updated = current with
        {
            State = NetworkTopologyGenerationState.Dirty,
            SourceRevision = checked(current.SourceRevision + 1),
            RowVersion = checked(current.RowVersion + 1),
            UpdatedAt = occurredAt,
        };
        rejection = NetworkTopologyEditRejection.None;
        return true;
    }

    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex StableFailureCodeRegex();
}
