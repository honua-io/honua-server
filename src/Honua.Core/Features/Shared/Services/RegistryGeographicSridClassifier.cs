// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Shared.Services;

/// <summary>
/// Default <see cref="IGeographicSridClassifier"/> implementation (#2794). Composes the live
/// <see cref="ICrsRegistry"/> with the static <see cref="GeographicSridClassifier"/> bootstrap
/// allowlist: the registry — which derives geographic-ness from <c>spatial_ref_sys</c> WKT/proj4
/// via <see cref="CrsDefinition.IsGeographic"/> — is authoritative, and the static lists answer
/// only when the registry cannot (SRID absent from the registry, or no provider registered an
/// <see cref="ICrsRegistry"/>).
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="ICrsRegistry"/> dependency is optional: read-only/analytics providers (DuckDB,
/// MySQL) do not register a registry, and in those deployments this classifier degrades cleanly to
/// the static allowlist, matching the pre-#2794 behaviour. When a registry is present, arbitrary
/// EPSG codes resolve correctly, including geographic codes outside the static list and geocentric
/// codes in the EPSG 4000–4999 block (classified as projected from their WKT), which the static
/// range heuristic could only approximate.
/// </para>
/// <para>
/// Registry lookups are cached inside the registry, so repeated classification of the same SRID is
/// cheap. Transient registry failures surface as a <see langword="null"/> definition and fall back
/// to the static answer rather than failing the caller.
/// </para>
/// </remarks>
public sealed class RegistryGeographicSridClassifier : IGeographicSridClassifier
{
    private readonly ICrsRegistry? _registry;

    /// <summary>
    /// Creates the classifier. The registry is optional so the service stays resolvable in
    /// provider configurations that do not register an <see cref="ICrsRegistry"/>.
    /// </summary>
    /// <param name="registry">
    /// Live CRS registry, or <see langword="null"/> to use the static allowlist exclusively.
    /// </param>
    public RegistryGeographicSridClassifier(ICrsRegistry? registry = null)
    {
        _registry = registry;
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsGeographicAsync(int srid, CancellationToken cancellationToken = default)
    {
        var registryAnswer = await TryResolveIsGeographicAsync(srid, cancellationToken).ConfigureAwait(false);
        return registryAnswer ?? GeographicSridClassifier.IsGeographicSrid(srid);
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsGeographicForMeasurementAsync(int srid, CancellationToken cancellationToken = default)
    {
        var registryAnswer = await TryResolveIsGeographicAsync(srid, cancellationToken).ConfigureAwait(false);
        return registryAnswer ?? GeographicSridClassifier.IsGeographicOrUnlistedGeographicRangeSrid(srid);
    }

    /// <summary>
    /// Resolves the registry's geographic classification for <paramref name="srid"/>, returning
    /// <see langword="null"/> when no registry is configured or the SRID is unknown to it.
    /// </summary>
    private async ValueTask<bool?> TryResolveIsGeographicAsync(int srid, CancellationToken cancellationToken)
    {
        if (_registry is null || srid <= 0)
        {
            return null;
        }

        var definition = await _registry.ResolveBySridAsync(srid, cancellationToken).ConfigureAwait(false);
        return definition?.IsGeographic;
    }
}
