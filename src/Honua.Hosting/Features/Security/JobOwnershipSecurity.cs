// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Infrastructure.Security;

/// <summary>Shared tenant and durable actor binding for background job lifecycles.</summary>
internal static class JobOwnershipSecurity
{
    internal static string? ResolveOwner(ClaimsPrincipal principal)
    {
        var actor = CanonicalSecurityActor.Resolve(principal);
        return actor is { IsDurablyRevalidatable: true }
            ? CanonicalSecurityActor.FindStampedValue(principal, CanonicalSecurityActor.CanonicalActorClaim) ?? actor.ActorId
            : null;
    }

    internal static bool IsAdministrator(ClaimsPrincipal principal)
        => principal.Identity?.IsAuthenticated == true && principal.IsInRole("admin");

    internal static bool CanAccess(OperationAuditInfo audit, string? requestTenant, ClaimsPrincipal principal)
    {
        if (audit.SubmitterSecurityContext is not { } submitter
            || !string.Equals(submitter.TenantId, requestTenant, StringComparison.Ordinal))
        {
            return false;
        }

        return IsAdministrator(principal)
            || MatchesSubmitter(audit, ResolveOwner(principal), requestTenant);
    }

    internal static bool MatchesSubmitter(OperationAuditInfo audit, string? owner, string? tenant)
        => !string.IsNullOrWhiteSpace(owner)
            && audit.SubmitterSecurityContext is { } submitter
            && string.Equals(submitter.TenantId, tenant, StringComparison.Ordinal)
            && string.Equals(audit.RequestedBy, owner, StringComparison.Ordinal);

    internal static string CreateJobId(string prefix, string? key, string? owner, string? tenant)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return $"{prefix}-{Guid.NewGuid():N}";
        }

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("job-owner-v1");
            WriteNullable(writer, tenant);
            WriteNullable(writer, owner);
            writer.Write(key.Trim());
        }

        var digest = SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
        return $"{prefix}-{Convert.ToHexStringLower(digest.AsSpan(0, 12))}";
    }

    private static void WriteNullable(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }
}
