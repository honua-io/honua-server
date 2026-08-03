// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>
/// Compact, metadata-only job artifact references used between the GP worker and result projector.
/// They contain no raster bytes, credentials, signed URLs, buckets, or connection strings.
/// </summary>
public static class RasterOutputArtifactReference
{
    private const string ManifestPrefix = "honua-raster-manifest:";
    private const string OutputPrefix = "honua-raster-output:";
    private const int MaximumReferenceLength = 48 * 1024;

    /// <summary>Creates a stable attempt-manifest marker for the durable job record.</summary>
    public static string CreateManifest(string storeReference, string manifestObjectKey)
    {
        if (!RasterOutputWorkerContract.IsLogicalStoreReference(storeReference)
            || !RasterOutputDescriptorValidator.IsSafeObjectKey(manifestObjectKey))
        {
            throw new ArgumentException("Raster output manifest reference is invalid.");
        }

        return ManifestPrefix + storeReference + ":" + manifestObjectKey;
    }

    /// <summary>Parses a worker-produced manifest marker.</summary>
    public static bool TryParseManifest(
        string? reference,
        out string storeReference,
        out string manifestObjectKey)
    {
        storeReference = string.Empty;
        manifestObjectKey = string.Empty;
        if (string.IsNullOrWhiteSpace(reference)
            || reference.Length > MaximumReferenceLength
            || !reference.StartsWith(ManifestPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var value = reference.AsSpan(ManifestPrefix.Length);
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        storeReference = value[..separator].ToString();
        manifestObjectKey = value[(separator + 1)..].ToString();
        return RasterOutputWorkerContract.IsLogicalStoreReference(storeReference)
            && RasterOutputDescriptorValidator.IsSafeObjectKey(manifestObjectKey);
    }

    /// <summary>Creates a durable, metadata-only reference for a visible raster output.</summary>
    public static string CreateOutput(RasterOutputDescriptor output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var validation = RasterOutputDescriptorValidator.Validate(output);
        if (!validation.IsValid)
        {
            throw new ArgumentException("Published raster output descriptor is invalid.", nameof(output));
        }

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(RasterOutputJson.Serialize(output)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var reference = OutputPrefix + encoded;
        if (reference.Length > MaximumReferenceLength)
        {
            throw new InvalidDataException("Published raster output reference exceeds its metadata ceiling.");
        }

        return reference;
    }

    /// <summary>Parses and validates a visible raster output reference.</summary>
    public static bool TryParseOutput(string? reference, out RasterOutputDescriptor? output)
    {
        output = null;
        if (string.IsNullOrWhiteSpace(reference)
            || reference.Length > MaximumReferenceLength
            || !reference.StartsWith(OutputPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var encoded = reference[OutputPrefix.Length..].Replace('-', '+').Replace('_', '/');
            var padded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var parsed = RasterOutputJson.Deserialize(json);
            if (!RasterOutputDescriptorValidator.Validate(parsed).IsValid)
            {
                return false;
            }

            output = parsed;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
