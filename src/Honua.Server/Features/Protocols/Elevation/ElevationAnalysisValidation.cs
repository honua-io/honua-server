// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;

namespace Honua.Server.Features.Protocols.Elevation;

internal static class ElevationAnalysisValidation
{
    internal readonly record struct ValidatedElevationDataset(
        bool IsValid,
        int LayerId,
        MetadataV2Object? Resource,
        IResult? ErrorResult);
    internal static (bool IsValid, double Longitude, double Latitude) ResolveCoordinate(double? lon, double? lat)
    {
        if (lon is not { } x || lat is not { } y)
        {
            return (false, 0, 0);
        }

        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return (false, 0, 0);
        }

        if (x < -180 || x > 180 || y < -90 || y > 90)
        {
            return (false, 0, 0);
        }

        return (true, x, y);
    }

    internal static (bool IsValid, double Height) ResolveHeight(double? height)
    {
        if (height is not { } value)
        {
            return (true, 0);
        }

        if (!double.IsFinite(value) || value < 0)
        {
            return (false, 0);
        }

        return (true, value);
    }

    internal static async Task<ValidatedElevationDataset> ValidateDatasetAsync(
        HttpContext context,
        string datasetId,
        CancellationToken cancellationToken)
    {
        var validation = int.TryParse(datasetId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId)
            ? await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
                context,
                layerId,
                LayerValidationHelpers.ValidationProtocol.OgcFeatures,
                AccessScope.Read,
                ServiceProtocols.Elevation,
                cancellationToken).ConfigureAwait(false)
            : await LayerValidationHelpers.ValidateCollectionWithAccessV2Async(
                context,
                datasetId,
                AccessScope.Read,
                ServiceProtocols.Elevation,
                cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!validation.IsValid)
        {
            return new ValidatedElevationDataset(false, 0, null, validation.ErrorResult);
        }

        // Fail closed when the publication has no storage layer or layer index rather than defaulting to layer 0.
        var resolvedLayerId = validation.Snapshot is not null && validation.Publication is not null
            ? validation.Snapshot.ResolveStorageLayerId(validation.Publication, validation.Resource)
            : validation.Publication?.LayerIndex;
        if (!resolvedLayerId.HasValue)
        {
            return new ValidatedElevationDataset(
                false,
                0,
                null,
                StandardErrorHelpers.CreateNotFound(context, $"Dataset '{datasetId}' not found."));
        }

        return new ValidatedElevationDataset(true, resolvedLayerId.Value, validation.Resource, null);
    }
}
