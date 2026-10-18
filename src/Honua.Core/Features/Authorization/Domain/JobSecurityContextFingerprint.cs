// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>Canonical identity for frozen policy inputs, separate from credential authority and expiry.</summary>
/// <remarks>
/// This hash is not an authentication proof. It must be bound to a server-captured or
/// server-sealed context. Credential snapshots and transient execution principals/deadlines
/// are excluded; credential authority requires its own canonical fingerprint and validation.
/// </remarks>
public static class JobSecurityContextFingerprint
{
    /// <summary>Hashes bounded owner, tenant, claim and role-alias inputs using versioned scalar encodings.</summary>
    /// <param name="context">The trusted original policy context.</param>
    /// <returns>A lowercase SHA-256 policy-context fingerprint.</returns>
    public static string Compute(JobSecurityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Claims is null || context.AgentPolicyRoleClaimTypes is null
            || context.Claims.Count > 256 || context.AgentPolicyRoleClaimTypes.Length > 256
            || context.Claims.Any(claim => claim is null || claim.Type is null || claim.Value is null)
            || context.AgentPolicyRoleClaimTypes.Any(alias => alias is null))
        {
            throw new UnauthorizedAccessException("The policy context exceeds its durable bounds.");
        }
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1);
            Write(writer, context.PrincipalId, 2048);
            Write(writer, context.TenantId, 512);
            Write(writer, context.RoleClaimType, 512);
            Write(writer, context.OwnerActorId, 2048);
            Write(writer, context.WorkspaceOwnerId, 2048);
            writer.Write(context.Claims.Count);
            var claimBytes = 0;
            foreach (var claim in context.Claims.OrderBy(claim => claim.Type, StringComparer.Ordinal).ThenBy(claim => claim.Value, StringComparer.Ordinal))
            {
                var typeBytes = Encoding.UTF8.GetByteCount(claim.Type);
                var valueBytes = Encoding.UTF8.GetByteCount(claim.Value);
                if (claimBytes > 65536 - typeBytes - valueBytes)
                {
                    throw new UnauthorizedAccessException("The policy context exceeds its durable bounds.");
                }
                claimBytes += typeBytes + valueBytes;
                Write(writer, claim.Type, 512);
                Write(writer, claim.Value, 16384);
            }
            writer.Write(context.AgentPolicyRoleClaimTypes.Length);
            foreach (var alias in context.AgentPolicyRoleClaimTypes.Order(StringComparer.Ordinal))
            {
                Write(writer, alias, 512);
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static void Write(BinaryWriter writer, string? value, int maximumBytes)
    {
        writer.Write(value is not null);
        if (value is null)
        {
            return;
        }
        if (Encoding.UTF8.GetByteCount(value) > maximumBytes)
        {
            throw new UnauthorizedAccessException("The policy context exceeds its durable bounds.");
        }
        writer.Write(value);
    }
}
