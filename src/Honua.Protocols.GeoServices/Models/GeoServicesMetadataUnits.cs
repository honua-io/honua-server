// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices.Models;

/// <summary>
/// Maps the service's geographic coordinate classification to GeoServices metadata units.
/// </summary>
internal static class GeoServicesMetadataUnits
{
    // Metadata without a resolvable CRS is advertised as WGS84 by both adapters.
    // The shared classifier covers known degree CRSs without mistaking geocentric codes
    // for geographic coordinates. Other CRSs retain the existing metre fallback.
    internal static string Resolve(MetadataV2SpatialReference? spatialReference)
        => spatialReference?.IsGeographic == true ||
           GeographicSridClassifier.IsGeographicSrid(spatialReference?.ResolveSrid())
            ? "esriDecimalDegrees"
            : "esriMeters";
}
