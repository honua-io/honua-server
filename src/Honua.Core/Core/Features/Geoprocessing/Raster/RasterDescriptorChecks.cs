// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Raster;

// Text checks shared by the raster source and raster output descriptor validators.
internal static class RasterDescriptorChecks
{
    internal static bool IsOpaqueReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            return false;
        }

        if (value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
        {
            return false;
        }

        return value[0] != '.' && !value.Contains("..", StringComparison.Ordinal);
    }

    internal static bool IsSafeObjectStoreKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !IsSafeText(value, 2048))
        {
            return false;
        }

        // S3 keys and Azure blob names legally contain spaces, '#', ':', repeated slashes, and
        // words such as "http-data". They remain inside the execution-owned provider/bucket VSI
        // envelope, so reject only syntax that can change that envelope or smuggle traversal.
        if (value[0] is '/' or '\\'
            || value.Contains('\\')
            || value.Contains('?')
            || value.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (!IsSafeText(decoded, 2048)
            || decoded[0] is '/' or '\\'
            || decoded.Contains('\\')
            || decoded.Contains('?')
            || decoded.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        return decoded
            .Split('/', StringSplitOptions.None)
            .All(segment => segment is not "." and not "..");
    }

    internal static bool IsMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 127 || value.Contains(';'))
        {
            return false;
        }

        var slash = value.IndexOf('/');
        return slash > 0
            && slash == value.LastIndexOf('/')
            && slash < value.Length - 1
            && value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '/' or '-' or '+' or '.' or '_');
    }

    internal static bool IsChecksum(RasterChecksum checksum)
    {
        var length = checksum.Algorithm switch
        {
            "sha256" => 64,
            "sha512" => 128,
            _ => 0,
        };

        return length > 0
            && checksum.Value is { } value
            && value.Length == length
            && value.All(Uri.IsHexDigit);
    }

    internal static bool IsSafeText(string value, int maximumLength) =>
        value.Length is > 0 && value.Length <= maximumLength
        && value.All(character => !char.IsControl(character));
}
