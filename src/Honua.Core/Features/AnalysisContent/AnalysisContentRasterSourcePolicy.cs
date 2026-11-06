// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Core.Features.AnalysisContent;

// Inline raster bytes are excluded because analysis content is metadata and its stores have no inline-payload admission boundary.
public static class AnalysisContentRasterSourcePolicy
{
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
