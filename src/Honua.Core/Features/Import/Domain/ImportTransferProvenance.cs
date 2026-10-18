// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Immutable transfer origin retained with a verified source across the queue boundary.
/// Used for accounting and identity checks, never as execution authority or a credential.
/// </summary>
/// <param name="UploadId">Original server-owned transfer identity.</param>
/// <param name="OwnerActorId">Actual executor owning the original transfer.</param>
/// <param name="TenantId">Original tenant, or null for a non-tenant deployment.</param>
/// <param name="TargetId">Original deployment target.</param>
/// <param name="WorkspaceId">Original workspace.</param>
/// <param name="TaskId">Original immutable task.</param>
/// <param name="TaskRevision">Original immutable task revision.</param>
/// <param name="TaskContentHash">Original immutable task content hash.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImportTransferProvenance(string UploadId, string OwnerActorId, string? TenantId,
    string TargetId, string WorkspaceId, string TaskId, long TaskRevision, string TaskContentHash)
{
    /// <summary>Validates the bounded provenance representation without granting access.</summary>
    public void Validate()
    {
        if (UploadId is null || UploadId.Length != 32 || UploadId.Any(static value => !char.IsAsciiHexDigitLower(value)) ||
            TaskContentHash is null || TaskContentHash.Length != 64 || TaskContentHash.Any(static value => !char.IsAsciiHexDigitLower(value)) ||
            TaskRevision < 1 || !IsText(OwnerActorId, 512) || TenantId is not null && !IsText(TenantId, 256) ||
            !IsText(TargetId, 256) || !IsText(WorkspaceId, 256) || !IsText(TaskId, 256))
            throw new ArgumentException("A complete immutable transfer origin is required.");
    }

    private static bool IsText(string value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && value == value.Trim() && !value.Any(char.IsControl);
}
