// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Authorization.Domain;

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
    JobSecurityContext ExecutorPolicyContext,
    string PolicyContextFingerprint,
    AgentCredentialSnapshot ExecutorAuthority);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RenewDatasetImportAuthority(
    [property: JsonRequired] string RenewalId,
    [property: JsonRequired] long ExpectedAuthorityRevision,
    [property: JsonRequired] string TaskContentHash);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReadDatasetImportAuthority([property: JsonRequired] string WorkspaceId, [property: JsonRequired] string TaskId,
    [property: JsonRequired] long Revision, [property: JsonRequired] string ContentHash);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RenewDatasetImportAuthorityCall([property: JsonRequired] string WorkspaceId, [property: JsonRequired] string TaskId,
    [property: JsonRequired] long Revision, [property: JsonRequired] RenewDatasetImportAuthority Request);

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
