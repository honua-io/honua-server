// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

public static class MigrationSourceKinds
{
    public const string GeoServicesRest = "geoservices-rest";

    // Clients and stored runs from before the neutral rename still send this value.
    public const string LegacyGeoServicesRest = "arcgis-geoservices-rest";

    public static bool IsGeoServicesRest(string? sourceKind)
        => string.Equals(sourceKind, GeoServicesRest, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceKind, LegacyGeoServicesRest, StringComparison.OrdinalIgnoreCase);
}
