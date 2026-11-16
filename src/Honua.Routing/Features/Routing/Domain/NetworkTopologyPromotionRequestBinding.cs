// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Routing.Features.Routing.Domain;

internal static class NetworkTopologyPromotionRequestBinding
{
    public static string Compute(
        NetworkTopologyPromotionKind kind,
        long targetGeneration,
        long expectedActiveGeneration,
        long expectedActiveRowVersion)
    {
        var material = $"{FormatKind(kind)}|{targetGeneration}|{expectedActiveGeneration}|{expectedActiveRowVersion}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

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
