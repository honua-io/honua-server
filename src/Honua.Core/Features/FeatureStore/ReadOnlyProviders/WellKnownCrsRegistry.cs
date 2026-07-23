// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

/// <summary>
/// CRS registry that resolves only the three universally well-known CRSes (OGC:CRS84,
/// EPSG:4326, EPSG:3857) with no backing catalog. Registered by read-only feature
/// providers (DuckDB, MySQL/MariaDB) so DI activation succeeds for
/// <c>Honua.Infrastructure.Services.SpatialReferenceResolver</c> — a mandatory, scoped
/// dependency of the FeatureServer, ImageServer, GeometryService, and gRPC protocol
/// adapters regardless of the active data-source provider.
/// </summary>
/// <remarks>
/// <para>
/// Found and fixed alongside <see cref="NoOpCrsDetectionService"/> under honua-server#2947
/// (secondary-provider HTTP-stack GA proof): with no <see cref="ICrsRegistry"/>
/// registration at all, any request that resolved <c>SpatialReferenceResolver</c> under
/// <c>DataSource:Provider=duckdb</c> or <c>mysql</c> failed DI activation outright, even
/// for the overwhelmingly common case of a layer stored (and queried) in its own default
/// SRID. Only <c>Honua.Postgres.Shared.PostgresCrsRegistry</c> ever registered an
/// implementation, because arbitrary-SRID resolution beyond the three well-known
/// definitions genuinely depends on Postgres's <c>spatial_ref_sys</c> catalog.
/// </para>
/// <para>
/// This is intentionally the same three built-in definitions
/// <c>PostgresCrsRegistry.TryGetBuiltInDefinition</c> falls back to before ever consulting
/// <c>spatial_ref_sys</c> — DuckDB/MySQL layers are essentially always configured in
/// WGS84 (4326) or Web Mercator (3857), so this covers the practical case honestly. Any
/// other SRID reports "not supported" rather than fabricating a definition, which is the
/// correct, capability-scoped answer for a provider with no CRS catalog to consult.
/// </para>
/// </remarks>
public sealed class WellKnownCrsRegistry : ICrsRegistry
{
    private const string Crs84Uri = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    private const string EpsgUriPrefix = "http://www.opengis.net/def/crs/EPSG/0/";
    private const string EpsgUrnPrefix = "urn:ogc:def:crs:EPSG::";
    private const string EpsgPrefix = "EPSG:";

    private static readonly CrsDefinition _crs84Definition = new(Crs84Uri, 4326, AxisOrder.EastNorth, true);
    private static readonly CrsDefinition _epsg4326Definition = new($"{EpsgUriPrefix}4326", 4326, AxisOrder.NorthEast, true);
    private static readonly CrsDefinition _epsg3857Definition = new($"{EpsgUriPrefix}3857", 3857, AxisOrder.EastNorth, false);

    /// <inheritdoc />
    public ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(crsIdentifier))
        {
            return ValueTask.FromResult<CrsDefinition?>(_crs84Definition);
        }

        var normalized = Normalize(crsIdentifier);
        if (string.Equals(normalized, Crs84Uri, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult<CrsDefinition?>(_crs84Definition);
        }

        return !TryParseSrid(normalized, out var srid)
            ? ValueTask.FromResult<CrsDefinition?>(null)
            : ResolveBySridAsync(srid, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(srid switch
        {
            4326 => (CrsDefinition?)_epsg4326Definition,
            3857 => _epsg3857Definition,
            _ => null,
        });

    /// <inheritdoc />
    public async ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
        => (await ResolveBySridAsync(srid, cancellationToken).ConfigureAwait(false)).HasValue;

    private static string Normalize(string crsIdentifier)
    {
        var trimmed = crsIdentifier.Trim();
        if (trimmed.Length > 1 && trimmed[0] == '<' && trimmed[^1] == '>')
        {
            trimmed = trimmed[1..^1];
        }

        if (string.Equals(trimmed, "CRS84", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "OGC:CRS84", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, Crs84Uri, StringComparison.OrdinalIgnoreCase))
        {
            return Crs84Uri;
        }

        if (trimmed.StartsWith(EpsgUrnPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return EpsgUriPrefix + trimmed[EpsgUrnPrefix.Length..];
        }

        if (trimmed.StartsWith(EpsgPrefix, StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith(EpsgUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return EpsgUriPrefix + trimmed[EpsgPrefix.Length..];
        }

        return trimmed;
    }

    private static bool TryParseSrid(string normalizedIdentifier, out int srid)
    {
        srid = 0;

        if (normalizedIdentifier.StartsWith(EpsgUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(
                normalizedIdentifier[EpsgUriPrefix.Length..],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out srid);
        }

        return int.TryParse(normalizedIdentifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid);
    }
}
