// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geometry.Domain;

namespace Honua.Core.Features.Geometry.Abstractions;

public interface IGeometryValidator
{
    GeometryValidationResult ValidateEsriJson(object? geometry);

    GeometryValidationResult ValidateWkb(byte[]? wkb);

    Task<GeometryValidationResult> ValidateTopologyAsync(byte[] wkb, CancellationToken cancellationToken = default);

    Task<GeometryRepairResult> RepairAsync(byte[] wkb, CancellationToken cancellationToken = default);

    Task<GeometryValidationResult> ValidateCompleteAsync(byte[] wkb, CancellationToken cancellationToken = default);
}
