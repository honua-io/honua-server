// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Tiles;

/// <summary>
/// Builds the collision-resistant path identity used to isolate service-local publication layers
/// in the generated tile cache.
/// </summary>
public static class TileCachePublicationScope
{
    /// <summary>
    /// Returns a stable, path-safe identity for one metadata publication.
    /// </summary>
    /// <param name="publicationId">Canonical metadata publication identifier.</param>
    /// <returns>The lower-case SHA-256 publication scope.</returns>
    public static string Create(string publicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicationId)))
            .ToLowerInvariant();
    }
}
