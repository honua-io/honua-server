// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Authentication;

namespace Honua.Infrastructure.Security;

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
            || MatchesSubmitter(audit, ResolveOwner(principal), requestTenant)
            || MatchesPriorFormatSubmitter(audit, principal, requestTenant);
    }

    internal static bool MatchesSubmitter(OperationAuditInfo audit, string? owner, string? tenant)
        => !string.IsNullOrWhiteSpace(owner)
            && audit.SubmitterSecurityContext is { } submitter
            && string.Equals(submitter.TenantId, tenant, StringComparison.Ordinal)
            && string.Equals(submitter.OwnerActorId, owner, StringComparison.Ordinal)
            && string.Equals(audit.RequestedBy, owner, StringComparison.Ordinal);

    // A legacy record matches the snapshot subject or key id, and a display name matches only when that snapshot has no subject.
    internal static bool MatchesPriorFormatSubmitter(OperationAuditInfo audit, ClaimsPrincipal principal, string? tenant)
    {
        if (audit.SubmitterSecurityContext is not { OwnerActorId: null } submitter
            || !string.Equals(submitter.TenantId, tenant, StringComparison.Ordinal)
            || !string.Equals(
                FindClaim(submitter, JobSecurityContextCapture.CapturedAuthenticationClaimType),
                bool.TrueString,
                StringComparison.Ordinal)
            || CanonicalSecurityActor.Resolve(principal) is not { IsDurablyRevalidatable: true } actor)
        {
            return false;
        }

        if (actor.ApiKeyId is { } apiKeyId)
        {
            return string.Equals(FindClaim(submitter, "api_key_id"), apiKeyId, StringComparison.Ordinal)
                && (string.Equals(audit.RequestedBy, apiKeyId, StringComparison.Ordinal)
                    || MatchesPriorTileApiKeyName(audit.RequestedBy, submitter));
        }

        return actor.SubjectId is { } subject
            && string.Equals(audit.RequestedBy, subject, StringComparison.Ordinal)
            && string.Equals(
                FindClaim(submitter, ClaimTypes.NameIdentifier) ?? FindClaim(submitter, "sub"),
                subject,
                StringComparison.Ordinal)
            && string.Equals(FindClaim(submitter, "iss"), actor.SubjectIssuer, StringComparison.Ordinal);
    }

    private static bool MatchesPriorTileApiKeyName(string? requestedBy, JobSecurityContext submitter)
        => !string.IsNullOrWhiteSpace(requestedBy)
            && FindClaim(submitter, ClaimTypes.NameIdentifier) is null
            && FindClaim(submitter, "sub") is null
            && string.Equals(FindClaim(submitter, ClaimTypes.Name), requestedBy, StringComparison.Ordinal);

    internal static string CreateJobId(string prefix, string? key, string? owner, string? tenant)
        => string.IsNullOrWhiteSpace(key)
            ? $"{prefix}-{Guid.NewGuid():N}"
            : $"{prefix}-{Convert.ToHexStringLower(Digest("job-owner-v1", key, owner, tenant).AsSpan(0, 12))}";

    internal static string CreatePriorFormatJobId(string prefix, string key)
        => $"{prefix}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())).AsSpan(0, 12))}";

    // The approval idempotency key includes the tenant and durable owner so two actors with the same client key do not share a proposal.
    internal static string? CreateOwnerScopedKey(string? key, string? owner, string? tenant)
        => string.IsNullOrWhiteSpace(key)
            ? null
            : $"owner-scoped-{Convert.ToHexStringLower(Digest("approval-owner-v1", key, owner, tenant).AsSpan(0, 16))}";

    private static byte[] Digest(string marker, string key, string? owner, string? tenant)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(marker);
            WriteNullable(writer, tenant);
            WriteNullable(writer, owner);
            writer.Write(key.Trim());
        }

        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    private static string? FindClaim(JobSecurityContext submitter, string type)
        => submitter.Claims.FirstOrDefault(claim => string.Equals(claim.Type, type, StringComparison.Ordinal))?.Value;

    private static void WriteNullable(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }
}
