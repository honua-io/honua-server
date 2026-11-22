// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public static class CloudRasterRegistrationValidation
{
    public const int MaxNameLength = 255;

    public const int MaxDescriptionLength = 4000;

    public const int MaxBucketLength = 255;

    public const int MaxObjectKeyLength = 1024;

    public static string? ValidateMetadata(int layerId, string? name, string? description)
    {
        if (layerId <= 0)
        {
            return "layerId must be a positive integer.";
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return $"name is required and must be {MaxNameLength} characters or fewer.";
        }

        if (ContainsControlCharacters(name))
        {
            return "name must not contain control characters.";
        }

        if (description is null)
        {
            return null;
        }

        if (description.Length > MaxDescriptionLength)
        {
            return $"description must be {MaxDescriptionLength} characters or fewer.";
        }

        return ContainsControlCharacters(description)
            ? "description must not contain control characters."
            : null;
    }

    public static string? ValidateStorageLocation(string? bucket, string? objectKey, string objectKeyFieldName)
    {
        if (string.IsNullOrWhiteSpace(bucket) || bucket.Length > MaxBucketLength)
        {
            return $"bucket is required and must be {MaxBucketLength} characters or fewer.";
        }

        if (!IsSafeBucket(bucket))
        {
            return "bucket contains characters that are not allowed.";
        }

        if (string.IsNullOrWhiteSpace(objectKey) || objectKey.Length > MaxObjectKeyLength)
        {
            return $"{objectKeyFieldName} is required and must be {MaxObjectKeyLength} characters or fewer.";
        }

        return IsSafeObjectKey(objectKey)
            ? null
            : $"{objectKeyFieldName} contains characters, URL schemes, or path segments that are not allowed.";
    }

    public static bool ContainsControlCharacters(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        foreach (var ch in value)
        {
            if (char.IsControl(ch) || ch is '\u2028' or '\u2029')
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsSafeBucket(string bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket) ||
            bucket.Length > MaxBucketLength ||
            bucket is "." ||
            bucket.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in bucket)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '.' or '_')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    public static bool IsSafeObjectKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxObjectKeyLength)
        {
            return false;
        }

        if (key.StartsWith('/') ||
            key.Contains('\\', StringComparison.Ordinal) ||
            key.Contains("..", StringComparison.Ordinal) ||
            key.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        return !ContainsControlCharacters(key);
    }
}
