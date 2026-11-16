// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Validation;

namespace Honua.Server.Features.Protocols.Elevation;

internal static class ElevationAnalysisValidation
{
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

    internal static Task<LayerValidationHelpers.MetadataV2ValidationResult> ValidateDatasetAsync(
        HttpContext context,
        string datasetId,
        CancellationToken cancellationToken)
    {
        if (int.TryParse(datasetId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            return LayerValidationHelpers.ValidateLayerWithAccessV2Async(
                context,
                layerId,
                LayerValidationHelpers.ValidationProtocol.OgcFeatures,
                AccessScope.Read,
                Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols.Elevation,
                cancellationToken);
        }

        return LayerValidationHelpers.ValidateCollectionWithAccessV2Async(
            context,
            datasetId,
            AccessScope.Read,
            Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols.Elevation,
            cancellationToken: cancellationToken);
    }
}
