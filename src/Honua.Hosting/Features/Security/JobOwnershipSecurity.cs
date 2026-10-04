// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Authentication;

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
            || MatchesSubmitter(audit, ResolveOwner(principal), requestTenant)
            || MatchesPriorFormatSubmitter(audit, principal, requestTenant);
    }

    internal static bool MatchesSubmitter(OperationAuditInfo audit, string? owner, string? tenant)
        => !string.IsNullOrWhiteSpace(owner)
            && audit.SubmitterSecurityContext is { } submitter
            && string.Equals(submitter.TenantId, tenant, StringComparison.Ordinal)
            && string.Equals(submitter.OwnerActorId, owner, StringComparison.Ordinal)
            && string.Equals(audit.RequestedBy, owner, StringComparison.Ordinal);

    /// <summary>
    /// Matches a record written before the durable owner actor was stored: its snapshot has no
    /// <see cref="JobSecurityContext.OwnerActorId"/> and <c>RequestedBy</c> holds the raw subject or
    /// API-key id. The caller owns it only when the snapshot was captured from an authenticated
    /// submitter whose subject and issuer, or API-key id, equal the caller's durable identity.
    /// A display name never matches.
    /// </summary>
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
            return string.Equals(audit.RequestedBy, apiKeyId, StringComparison.Ordinal)
                && string.Equals(FindClaim(submitter, "api_key_id"), apiKeyId, StringComparison.Ordinal);
        }

        return actor.SubjectId is { } subject
            && string.Equals(audit.RequestedBy, subject, StringComparison.Ordinal)
            && string.Equals(
                FindClaim(submitter, ClaimTypes.NameIdentifier) ?? FindClaim(submitter, "sub"),
                subject,
                StringComparison.Ordinal)
            && string.Equals(FindClaim(submitter, "iss"), actor.SubjectIssuer, StringComparison.Ordinal);
    }

    internal static string CreateJobId(string prefix, string? key, string? owner, string? tenant)
        => string.IsNullOrWhiteSpace(key)
            ? $"{prefix}-{Guid.NewGuid():N}"
            : $"{prefix}-{Convert.ToHexStringLower(Digest("job-owner-v1", key, owner, tenant).AsSpan(0, 12))}";

    /// <summary>Key-only job id that keyed submissions derived before ids were scoped by tenant and owner.</summary>
    internal static string CreatePriorFormatJobId(string prefix, string key)
        => $"{prefix}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())).AsSpan(0, 12))}";

    /// <summary>
    /// Approval-proposal idempotency key scoped to the tenant and durable owner, so two actors
    /// presenting the same client key never resolve to one proposal.
    /// </summary>
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
