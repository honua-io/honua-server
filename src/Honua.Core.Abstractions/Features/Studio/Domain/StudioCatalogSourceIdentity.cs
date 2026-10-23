// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Studio.Domain;

/// <summary>Canonical Studio catalog identity; an identity grants no source access.</summary>
public static class StudioCatalogSourceIdentity
{
    // Publishing accepts a 64-character service name. SanitizeMetadataId keeps Unicode letters,
    // and the stored id is "svc-publish-" plus that name. A non-ASCII BMP letter is three UTF-8
    // bytes, each percent-encoded as three characters. The layer index is a non-negative int.
    private const int MaxPublishedServiceNameLength = 64;
    private const int PublishedServiceIdPrefixLength = 12;
    private const int MaxEscapedCharsPerServiceCharacter = 9;
    private const int MaxLayerIndexLength = 10;

    /// <summary>
    /// Longest identity <see cref="Create"/> returns. The Studio encodeURIComponent form of a
    /// published service fits. A longer service id uses a digest that is not a truncation of
    /// another service's identity.
    /// </summary>
    public const int MaxLength =
        PublishedServiceIdPrefixLength
        + (MaxPublishedServiceNameLength * MaxEscapedCharsPerServiceCharacter)
        + 1
        + MaxLayerIndexLength;

    /// <summary>Encodes the service identity and service-local layer index as Studio does.</summary>
    /// <param name="serviceId">Canonical service identity, not its display name or URL.</param>
    /// <param name="layerId">Service-local layer index.</param>
    /// <returns>
    /// The ECMAScript encodeURIComponent-compatible catalog identity, or a digest form when that
    /// encoding would exceed <see cref="MaxLength"/>.
    /// </returns>
    public static string Create(string serviceId, int layerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentOutOfRangeException.ThrowIfNegative(layerId);
        var layer = layerId.ToString(CultureInfo.InvariantCulture);
        var encoded = EncodeService(serviceId) + "/" + layer;
        if (encoded.Length <= MaxLength)
        {
            return encoded;
        }

        // '#' is percent-encoded by EncodeService, so this form cannot equal a Studio identity.
        // The digest covers the exact service id and layer index, not a shared prefix.
        return "#" + Digest(serviceId, layer) + "/" + layer;
    }

    private static string EncodeService(string serviceId)
        => Uri.EscapeDataString(serviceId)
            .Replace("%21", "!", StringComparison.Ordinal)
            .Replace("%27", "'", StringComparison.Ordinal)
            .Replace("%28", "(", StringComparison.Ordinal)
            .Replace("%29", ")", StringComparison.Ordinal)
            .Replace("%2A", "*", StringComparison.Ordinal);

    private static string Digest(string serviceId, string layer)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(serviceId.Length.ToString(CultureInfo.InvariantCulture)));
        hash.AppendData("="u8);
        hash.AppendData(Encoding.UTF8.GetBytes(serviceId));
        hash.AppendData("\n"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(layer));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
