// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#pragma warning disable CA1716 // Preserve the stable Shared namespace used across existing contracts.
namespace Honua.Core.Features.Shared.Models;

public interface IGeographicSridClassifier
{
    ValueTask<bool> IsGeographicAsync(int srid, CancellationToken cancellationToken = default);

    ValueTask<bool> IsGeographicForMeasurementAsync(int srid, CancellationToken cancellationToken = default);

    ValueTask<double?> TryResolveLinearUnitFactorAsync(int srid, CancellationToken cancellationToken = default);
}
