// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.AgentWorkspace;

// Server-retained authority is deliberately separate from DatasetImportTask content.
// A refreshed access token must not change immutable upload or task identity.
internal sealed record DatasetImportExecutionBinding(
    string TaskId,
    long TaskRevision,
    string TaskContentHash,
    long AuthorityRevision,
    string AuthorityFingerprint,
    string ExecutorActorId,
    string? InitiatingHumanActorId,
    string OriginatingTurnId,
    string OriginalTurnHash,
    AgentCredentialSnapshot ExecutorAuthority);

internal sealed record RenewDatasetImportAuthority(
    string RenewalId,
    long ExpectedAuthorityRevision,
    string TaskContentHash);

internal sealed record DatasetImportAuthorityRenewal(
    string RenewalId,
    long PreviousAuthorityRevision,
    long AuthorityRevision,
    string AuthorityFingerprint,
    DateTimeOffset RenewedAt);

internal static class DatasetImportExecutionAuthority
{
    // Token expiry is the sole renewable dimension. Every authority lineage field
    // remains exact, including key version, profile generation and canonical grants.
    internal static bool SameLineage(AgentCredentialSnapshot original, AgentCredentialSnapshot refreshed)
        => original.Kind == refreshed.Kind && original.Id == refreshed.Id &&
            original.CredentialVersion == refreshed.CredentialVersion && original.Generation == refreshed.Generation &&
            original.ActorId == refreshed.ActorId && original.Binding.SequenceEqual(refreshed.Binding, StringComparer.Ordinal) &&
            SameSet(original.OAuthScopes, refreshed.OAuthScopes) &&
            SameSet(original.RoleGrantFingerprints, refreshed.RoleGrantFingerprints) &&
            SameSet(original.MembershipRoles, refreshed.MembershipRoles) && SameSet(original.Permissions, refreshed.Permissions);

    private static bool SameSet(string[] original, string[] refreshed)
        => original.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .SequenceEqual(refreshed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal);
}
