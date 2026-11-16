// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

public sealed class ReadOnlyGeometryTopologyValidator : IGeometryTopologyValidator
{
    private readonly string _providerName;

    public ReadOnlyGeometryTopologyValidator(string providerName)
    {
        _providerName = providerName;
    }

    public Task<GeometryValidationResult> ValidateTopologyAsync(byte[] wkb, CancellationToken cancellationToken = default)
        => Task.FromResult(GeometryValidationResult.Success());

    public Task<GeometryRepairResult> RepairAsync(byte[] wkb, CancellationToken cancellationToken = default)
        => Task.FromResult(GeometryRepairResult.Failed(
            $"{_providerName} provider is read-only. Geometry repair is not supported."));
}
