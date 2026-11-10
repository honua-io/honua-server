// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Infrastructure.Crs;

public readonly record struct DatumAreaEnvelope(double West, double South, double East, double North);

public interface IDatumTransformationCatalog
{
    bool TryGetDefault(int fromSrid, int toSrid, [NotNullWhen(true)] out DatumTransformationSelection? selection);

    bool TryGetForEnvelope(
        int fromSrid,
        int toSrid,
        double west,
        double south,
        double east,
        double north,
        [NotNullWhen(true)] out DatumTransformationSelection? selection)
        => TryGetDefault(fromSrid, toSrid, out selection);

    // A pair with area-of-use bounds must not fall back to the default grid on a non-match.
    bool HasAreaOfUse(int fromSrid, int toSrid) => false;

    bool TryGetByWkid(int wkid, int fromSrid, int toSrid, [NotNullWhen(true)] out DatumTransformationSelection? selection);
}
