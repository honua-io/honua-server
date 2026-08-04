// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Core.Features.AnalysisContent;

/// <summary>
/// Enforces the raster contract at every durable analysis-package boundary.
/// </summary>
/// <remarks>
/// Reference descriptors may be stored for forward-compatible authoring and round trips.
/// Inline raster bytes are deliberately excluded: analysis content is metadata, not a raster
/// data plane, and its stores do not have a configurable inline-payload admission boundary.
/// </remarks>
public static class AnalysisContentRasterSourcePolicy
{
    /// <summary>Validates a package before hashing or durable persistence.</summary>
    public static RasterSourceValidationResult ValidateForPersistence(
        AnalysisPackageContent package,
        RasterSourceValidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Plan is null)
        {
            return new RasterSourceValidationResult
            {
                Errors =
                [
                    new RasterSourceValidationError(
                        RasterSourceValidationCodes.InvalidField,
                        "plan",
                        "Analysis package plan is required."),
                ],
            };
        }

        var validation = RasterSourcePlanValidator.Validate(package.Plan, options, cancellationToken);
        var errors = validation.Errors.ToList();
        if (package.Plan.Steps is null
            || errors.Any(error => error.Code == RasterSourceValidationCodes.TooManySources))
        {
            return new RasterSourceValidationResult { Errors = errors };
        }

        foreach (var step in package.Plan.Steps)
        {
            if (step?.RasterSources is null)
            {
                continue;
            }

            foreach (var source in step.RasterSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source.Value is InlineRasterSourceDescriptor)
                {
                    errors.Add(new RasterSourceValidationError(
                        RasterSourceValidationCodes.InlinePersistenceDenied,
                        source.Key,
                        "Inline raster bytes cannot be persisted in analysis content; use a PostGIS, object-store, Zarr, or staged-artifact reference."));
                }
            }
        }

        return new RasterSourceValidationResult { Errors = errors };
    }
}
