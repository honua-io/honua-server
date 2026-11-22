// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Multidimensional.Domain;

public static class MultidimensionalCoverageValidation
{
    public const int MaxBucketLength = CloudRasterRegistrationValidation.MaxBucketLength;

    public const int MaxObjectKeyLength = CloudRasterRegistrationValidation.MaxObjectKeyLength;

    public const int MaxVariableNameLength = 255;

    public const int MaxVariables = 64;

    public const int MaxNameLength = CloudRasterRegistrationValidation.MaxNameLength;

    public const int MaxDescriptionLength = CloudRasterRegistrationValidation.MaxDescriptionLength;

    private static readonly string[] AllowedHdfExtensions = [".h5", ".hdf5"];
    private static readonly string[] AllowedNetCdfExtensions = [".nc", ".nc4"];
    private static readonly string[] AllowedGribExtensions = [".grib", ".grb", ".grb2", ".grib2"];

    public static bool TryValidate(
        MultidimensionalCoverageRegistrationRequest request,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (CloudRasterRegistrationValidation.ValidateMetadata(request.LayerId, request.Name, request.Description) is { } metadataError)
        {
            error = metadataError;
            return false;
        }

        if (!Enum.IsDefined(request.Format))
        {
            error = "format must be one of: CloudOptimizedHdf5, NetCdf4, Grib.";
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

        if (CloudRasterRegistrationValidation.ValidateStorageLocation(request.Bucket, request.ObjectKey, "objectKey") is { } storageError)
        {
            error = storageError;
            return false;
        }

        if (!ExtensionMatchesFormat(request.ObjectKey, request.Format))
        {
            error = request.Format switch
            {
                MultidimensionalCoverageFormat.CloudOptimizedHdf5 =>
                    "objectKey must end with .h5 or .hdf5 for CloudOptimizedHdf5 format.",
                MultidimensionalCoverageFormat.Grib =>
                    "objectKey must end with .grib, .grb, .grb2, or .grib2 for Grib format.",
                _ => "objectKey must end with .nc or .nc4 for NetCdf4 format."
            };
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
            MultidimensionalCoverageFormat.Grib => AllowedGribExtensions,
            _ => Array.Empty<string>()
        };

        return allowed.Any(ext => objectKey.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
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
