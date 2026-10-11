// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Binds a promotion or rollback idempotency key to the request that first used it.
/// </summary>
internal static class NetworkTopologyPromotionRequestBinding
{
    /// <summary>
    /// SHA-256 hex of <c>kind|targetGeneration|expectedActiveGeneration|expectedActiveRowVersion</c>.
    /// </summary>
    public static string Compute(
        NetworkTopologyPromotionKind kind,
        long targetGeneration,
        long expectedActiveGeneration,
        long expectedActiveRowVersion)
    {
        var material = $"{FormatKind(kind)}|{targetGeneration}|{expectedActiveGeneration}|{expectedActiveRowVersion}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    /// <summary>
    /// Accepts a stored row when it is the same request. A legacy row with no hash replays only
    /// when the kind and generations match. Any other reuse throws
    /// <see cref="NetworkTopologyPromotionRejection.IdempotencyKeyReused"/>.
    /// </summary>
    public static void EnsureCompatible(
        NetworkTopologyPromotionRecord existing,
        NetworkTopologyPromotionKind kind,
        long targetGeneration,
        long expectedActiveGeneration,
        long expectedActiveRowVersion)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (existing.RequestHash is null)
        {
            if (existing.Kind == kind
                && existing.ToGeneration == targetGeneration
                && (existing.FromGeneration is null || existing.FromGeneration == expectedActiveGeneration))
            {
                return;
            }

            throw Conflict();
        }

        var hash = Compute(kind, targetGeneration, expectedActiveGeneration, expectedActiveRowVersion);
        if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
        {
            throw Conflict();
        }
    }

    private static NetworkTopologyPromotionConflictException Conflict() =>
        new(
            NetworkTopologyPromotionRejection.IdempotencyKeyReused,
            "The idempotency key was already used for a different topology promotion request.");

    private static string FormatKind(NetworkTopologyPromotionKind kind) => kind switch
    {
        NetworkTopologyPromotionKind.Promote => "promote",
        NetworkTopologyPromotionKind.Rollback => "rollback",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unrecognised promotion kind."),
    };
}
