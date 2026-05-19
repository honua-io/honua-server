// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Raster.Multidimensional.Domain;

/// <summary>
/// Server-side validation for multidimensional coverage registration input.
/// Keeps URL/path safety, length limits, and variable selection rules in one
/// place so admin endpoints and gRPC adapters share identical rejection
/// behavior.
/// </summary>
public static class MultidimensionalCoverageValidation
{
    /// <summary>Maximum bucket name length (S3 cap + Azure container cap).</summary>
    public const int MaxBucketLength = 255;

    /// <summary>Maximum object key length (S3 cap).</summary>
    public const int MaxObjectKeyLength = 1024;

    /// <summary>Maximum length for a variable name (CF/CDM allow up to 255).</summary>
    public const int MaxVariableNameLength = 255;

    /// <summary>Maximum number of operator-declared variable names per registration.</summary>
    public const int MaxVariables = 64;

    /// <summary>Maximum length of the registration name.</summary>
    public const int MaxNameLength = 255;

    /// <summary>Maximum length of an optional description.</summary>
    public const int MaxDescriptionLength = 4000;

    private static readonly string[] AllowedHdfExtensions = [".h5", ".hdf5"];
    private static readonly string[] AllowedNetCdfExtensions = [".nc", ".nc4"];

    /// <summary>
    /// Validates a registration request. Returns <c>true</c> when the request
    /// is acceptable; otherwise sets <paramref name="error"/> to a stable,
    /// non-leaking explanation suitable for an admin client.
    /// </summary>
    public static bool TryValidate(
        MultidimensionalCoverageRegistrationRequest request,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.LayerId <= 0)
        {
            error = "layerId must be a positive integer.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > MaxNameLength)
        {
            error = $"name is required and must be {MaxNameLength} characters or fewer.";
            return false;
        }

        if (request.Description is { Length: > MaxDescriptionLength })
        {
            error = $"description must be {MaxDescriptionLength} characters or fewer.";
            return false;
        }

        if (!Enum.IsDefined(request.Format))
        {
            error = "format must be one of: CloudOptimizedHdf5, NetCdf4.";
            return false;
        }

        if (!Enum.IsDefined(request.Provider))
        {
            error = "provider must be one of: AwsS3, AzureBlob.";
            return false;
        }

        if (request.Provider == CloudStorageProvider.Local)
        {
            error = "Local storage is not supported for cloud-optimized HDF/NetCDF coverages.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Bucket) || request.Bucket.Length > MaxBucketLength)
        {
            error = $"bucket is required and must be {MaxBucketLength} characters or fewer.";
            return false;
        }

        if (!IsSafeBucket(request.Bucket))
        {
            error = "bucket contains characters that are not allowed.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.ObjectKey) || request.ObjectKey.Length > MaxObjectKeyLength)
        {
            error = $"objectKey is required and must be {MaxObjectKeyLength} characters or fewer.";
            return false;
        }

        if (!IsSafeObjectKey(request.ObjectKey))
        {
            error = "objectKey contains characters or path segments that are not allowed.";
            return false;
        }

        if (!ExtensionMatchesFormat(request.ObjectKey, request.Format))
        {
            error = request.Format == MultidimensionalCoverageFormat.CloudOptimizedHdf5
                ? "objectKey must end with .h5 or .hdf5 for CloudOptimizedHdf5 format."
                : "objectKey must end with .nc or .nc4 for NetCdf4 format.";
            return false;
        }

        if (request.Variables is null)
        {
            error = "variables is required (use an empty array to expose every data variable).";
            return false;
        }

        if (request.Variables.Count > MaxVariables)
        {
            error = $"variables must contain at most {MaxVariables} entries.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variable in request.Variables)
        {
            if (string.IsNullOrWhiteSpace(variable) || variable.Length > MaxVariableNameLength)
            {
                error = $"every variable name must be non-empty and {MaxVariableNameLength} characters or fewer.";
                return false;
            }

            if (!IsSafeVariableName(variable))
            {
                error = $"variable '{variable}' contains characters that are not allowed.";
                return false;
            }

            if (!seen.Add(variable))
            {
                error = $"variable '{variable}' is duplicated.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool ExtensionMatchesFormat(string objectKey, MultidimensionalCoverageFormat format)
    {
        var allowed = format switch
        {
            MultidimensionalCoverageFormat.CloudOptimizedHdf5 => AllowedHdfExtensions,
            MultidimensionalCoverageFormat.NetCdf4 => AllowedNetCdfExtensions,
            _ => Array.Empty<string>()
        };

        foreach (var ext in allowed)
        {
            if (objectKey.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSafeBucket(string bucket)
    {
        foreach (var ch in bucket)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '.' or '_')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool IsSafeObjectKey(string key)
    {
        if (key.StartsWith('/'))
        {
            return false;
        }

        if (key.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in key)
        {
            if (ch is < (char)32 or (char)127)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeVariableName(string variable)
    {
        foreach (var ch in variable)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or '/')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
