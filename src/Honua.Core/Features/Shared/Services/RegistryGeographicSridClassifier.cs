// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Shared.Services;

// The live registry is authoritative, and the static allowlist answers only when the registry cannot.
public sealed class RegistryGeographicSridClassifier : IGeographicSridClassifier
{
    private readonly ICrsRegistry? _registry;

    public RegistryGeographicSridClassifier(ICrsRegistry? registry = null)
    {
        _registry = registry;
    }

    public async ValueTask<bool> IsGeographicAsync(int srid, CancellationToken cancellationToken = default)
    {
        var registryAnswer = await TryResolveIsGeographicAsync(srid, cancellationToken).ConfigureAwait(false);
        return registryAnswer ?? GeographicSridClassifier.IsGeographicSrid(srid);
    }

    public async ValueTask<bool> IsGeographicForMeasurementAsync(int srid, CancellationToken cancellationToken = default)
    {
        var registryAnswer = await TryResolveIsGeographicAsync(srid, cancellationToken).ConfigureAwait(false);
        return registryAnswer ?? GeographicSridClassifier.IsGeographicOrUnlistedGeographicRangeSrid(srid);
    }

    private async ValueTask<bool?> TryResolveIsGeographicAsync(int srid, CancellationToken cancellationToken)
    {
        if (_registry is null || srid <= 0)
        {
            return null;
        }

        var definition = await _registry.ResolveBySridAsync(srid, cancellationToken).ConfigureAwait(false);
        return definition?.IsGeographic;
    }

    public async ValueTask<double?> TryResolveLinearUnitFactorAsync(int srid, CancellationToken cancellationToken = default)
    {
        if (_registry is null || srid <= 0)
        {
            return null;
        }

        var definition = await _registry.ResolveBySridAsync(srid, cancellationToken).ConfigureAwait(false);
        if (definition is { IsGeographic: false, LinearUnitFactor: double factor }
            && factor > 0
            && double.IsFinite(factor))
        {
            return factor;
        }

        return null;
    }
}
