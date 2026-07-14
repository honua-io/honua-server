// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Validates topology-generation lifecycle changes without depending on a storage provider.
/// </summary>
public static partial class NetworkTopologyLifecycle
{
    /// <summary>
    /// Returns whether <paramref name="target"/> is a legal successor to
    /// <paramref name="current"/>. Active generations can only be retired; content
    /// writers therefore cannot mutate the live solve target.
    /// </summary>
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
            (NetworkTopologyGenerationState.Failed, NetworkTopologyGenerationState.Dirty) => true,
            (NetworkTopologyGenerationState.Failed, NetworkTopologyGenerationState.Retired) => true,
            _ => false,
        };

    /// <summary>
    /// Applies a provider-neutral compare-and-swap lifecycle transition.
    /// </summary>
    /// <param name="current">Current generation metadata.</param>
    /// <param name="expectedRowVersion">Row version observed by the caller.</param>
    /// <param name="target">Requested successor state.</param>
    /// <param name="occurredAt">Authoritative transition timestamp.</param>
    /// <param name="failureCode">Stable failure code when transitioning to <c>Failed</c>.</param>
    /// <param name="updated">Updated generation on success; otherwise <paramref name="current"/>.</param>
    /// <param name="failure">Stable rejection reason.</param>
    /// <returns><see langword="true"/> when the transition was applied.</returns>
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

    /// <summary>
    /// Applies a provider-neutral compare-and-swap content edit (#2716). Unlike
    /// <see cref="TryTransition"/>, this does not move between arbitrary lifecycle states —
    /// it only ever leaves a generation in <see cref="NetworkTopologyGenerationState.Dirty"/>,
    /// bumping both the row version (optimistic concurrency) and the source revision (content
    /// clock). A <c>draft</c> generation transitions to <c>dirty</c> on its first edit; a
    /// generation that is already <c>dirty</c> stays <c>dirty</c>. Any other state (building,
    /// ready, active, failed, retired) rejects the edit so content writers can never mutate a
    /// generation that is being built, promotable, live, or terminal.
    /// </summary>
    /// <param name="current">Current generation metadata.</param>
    /// <param name="expectedRowVersion">Row version observed by the caller (e.g. via <c>If-Match</c>).</param>
    /// <param name="occurredAt">Authoritative mutation timestamp.</param>
    /// <param name="updated">Updated generation on success; otherwise <paramref name="current"/>.</param>
    /// <param name="rejection">Stable rejection reason.</param>
    /// <returns><see langword="true"/> when the content edit was applied.</returns>
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
