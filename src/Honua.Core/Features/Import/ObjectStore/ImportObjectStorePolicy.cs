// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Import.ObjectStore;

/// <summary>
/// Parses an <c>s3://</c> source and applies the bucket and role allow-lists.
/// A denial does not call the cloud.
/// </summary>
public static class ImportObjectStorePolicy
{
    private const int MaxSourceUriLength = 2048;

    /// <summary>
    /// Admits <paramref name="sourceUri"/> when the scheme is <c>s3</c>, the bucket is
    /// allow-listed, and <paramref name="assumeRoleArn"/> is either omitted or allow-listed.
    /// </summary>
    public static bool TryAdmit(
        string? sourceUri,
        string? assumeRoleArn,
        ImportObjectStoreOptions options,
        out ImportObjectLocation? location,
        out ImportObjectStoreProblem? problem)
    {
        ArgumentNullException.ThrowIfNull(options);
        location = null;

        if (!TryParse(sourceUri, out var parsed, out problem))
        {
            return false;
        }

        if (!IsBucketAllowed(parsed!.Bucket, options.AllowedBuckets))
        {
            problem = Forbidden($"Bucket '{parsed.Bucket}' is not allow-listed.");
            return false;
        }

        var roleArn = string.IsNullOrWhiteSpace(assumeRoleArn) ? null : assumeRoleArn.Trim();
        if (roleArn is not null && !IsRoleAllowed(roleArn, options.AssumeRoles))
        {
            problem = Forbidden("assumeRoleArn is not allow-listed.");
            return false;
        }

        location = parsed with { AssumeRoleArn = roleArn };
        problem = null;
        return true;
    }

    /// <summary>Whether <paramref name="bucket"/> matches an exact name or a trailing-<c>*</c> prefix.</summary>
    public static bool IsBucketAllowed(string bucket, IEnumerable<string>? patterns)
    {
        if (string.IsNullOrWhiteSpace(bucket) || patterns is null)
        {
            return false;
        }

        foreach (var raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var pattern = raw.Trim();
            if (pattern.EndsWith('*'))
            {
                var prefix = pattern[..^1];
                if (bucket.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(bucket, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="roleArn"/> equals a configured role ARN.</summary>
    public static bool IsRoleAllowed(string roleArn, IEnumerable<ImportObjectStoreRoleOptions>? roles)
    {
        if (string.IsNullOrWhiteSpace(roleArn) || roles is null)
        {
            return false;
        }

        foreach (var role in roles)
        {
            if (string.Equals(role.RoleArn?.Trim(), roleArn.Trim(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParse(
        string? sourceUri,
        out ImportObjectLocation? location,
        out ImportObjectStoreProblem? problem)
    {
        location = null;
        var candidate = sourceUri?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            problem = Invalid("sourceUri is required.");
            return false;
        }

        if (candidate.Length > MaxSourceUriLength)
        {
            problem = Invalid("sourceUri is too long.");
            return false;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Scheme))
        {
            problem = Unsupported();
            return false;
        }

        if (!string.Equals(uri.Scheme, "s3", StringComparison.OrdinalIgnoreCase))
        {
            problem = Unsupported();
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            problem = Invalid("sourceUri must not embed credentials.");
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            problem = Invalid("sourceUri must not include a query or fragment.");
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            problem = Invalid("sourceUri must include a bucket and an object key.");
            return false;
        }

        // Uri.AbsolutePath removes '.' and '..' before IsSafeKey can see them, so
        // s3://bucket/a/../secret.geojson would be admitted as secret.geojson.
        // Reject dot segments on the raw path, and reject any path Uri rewrites.
        if (!TryReadRawKey(candidate, out var rawKey)
            || !TryUnescape(rawKey, out var decodedKey)
            || HasDotSegment(rawKey)
            || HasDotSegment(decodedKey)
            || !string.Equals(decodedKey, CanonicalKey(uri), StringComparison.Ordinal))
        {
            problem = Invalid("sourceUri must not contain '.' or '..' path segments.");
            return false;
        }

        var key = decodedKey;
        if (string.IsNullOrEmpty(key) || !IsSafeKey(key))
        {
            problem = Invalid("sourceUri must include a bucket and an object key.");
            return false;
        }

        location = new ImportObjectLocation
        {
            Bucket = uri.Host,
            Key = key,
            SourceUri = candidate,
        };
        problem = null;
        return true;
    }

    /// <summary>
    /// Path after the bucket, without the separator slash and without a query or fragment.
    /// Taken from the original text so '.' and '..' are still present.
    /// </summary>
    private static bool TryReadRawKey(string candidate, out string rawKey)
    {
        rawKey = string.Empty;
        var scheme = candidate.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            return false;
        }

        var authority = scheme + 3;
        var path = candidate.IndexOf('/', authority);
        if (path < 0)
        {
            return true;
        }

        var rest = candidate[(path + 1)..];
        var cut = rest.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            rest = rest[..cut];
        }

        rawKey = rest;
        return true;
    }

    private static string CanonicalKey(Uri uri)
        => TryUnescape(uri.AbsolutePath.TrimStart('/'), out var key) ? key : string.Empty;

    private static bool TryUnescape(string value, out string unescaped)
    {
        try
        {
            unescaped = Uri.UnescapeDataString(value);
            return true;
        }
        catch (UriFormatException)
        {
            unescaped = string.Empty;
            return false;
        }
    }

    private static bool HasDotSegment(string path)
    {
        var start = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            if (i != path.Length && path[i] != '/')
            {
                continue;
            }

            var segment = path[start..i];
            if (segment is "." or "..")
            {
                return true;
            }

            start = i + 1;
        }

        return false;
    }

    private static bool IsSafeKey(string key)
    {
        if (key.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = key.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    private static ImportObjectStoreProblem Unsupported()
        => new()
        {
            StatusCode = 400,
            Title = "Object store scheme is not supported",
            Detail = "Only s3:// sources are supported.",
            Code = ImportObjectStoreProblems.UnsupportedScheme,
        };

    private static ImportObjectStoreProblem Invalid(string detail)
        => new()
        {
            StatusCode = 400,
            Title = "Invalid object store source",
            Detail = detail,
        };

    private static ImportObjectStoreProblem Forbidden(string detail)
        => new()
        {
            StatusCode = 403,
            Title = "Object store source forbidden",
            Detail = detail,
            Code = ImportObjectStoreProblems.Forbidden,
        };
}
