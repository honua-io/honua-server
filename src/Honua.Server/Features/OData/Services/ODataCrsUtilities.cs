// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.OData.Models;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Helpers for resolving CRS details for OData spatial payloads.
/// </summary>
internal static class ODataCrsUtilities
{
    private const string CrsTypeName = "name";

    public static async ValueTask<AxisOrder> ResolveAxisOrderAsync(
        ICrsRegistry crsRegistry,
        int? srid,
        CancellationToken cancellationToken)
    {
        if (!srid.HasValue || srid.Value <= 0)
        {
            return AxisOrder.EastNorth;
        }

        if (srid.Value == SpatialReference.WGS84.Wkid)
        {
            return AxisOrder.EastNorth;
        }

        var definition = await crsRegistry.ResolveBySridAsync(srid.Value, cancellationToken).ConfigureAwait(false);
        return definition?.AxisOrder ?? AxisOrder.EastNorth;
    }

    public static ValueTask<(bool IsValid, CrsDefinition? Definition, string? ErrorMessage)> TryResolveGeometryCrsAsync(
        ICrsRegistry crsRegistry,
        ODataSpatialGeometry? geometry,
        CancellationToken cancellationToken)
        => TryResolveGeometryCrsAsync(crsRegistry, geometry, null, cancellationToken);

    public static async ValueTask<(bool IsValid, CrsDefinition? Definition, string? ErrorMessage)> TryResolveGeometryCrsAsync(
        ICrsRegistry crsRegistry,
        ODataSpatialGeometry? geometry,
        int? requiredSrid,
        CancellationToken cancellationToken)
    {
        if (geometry?.Crs == null)
        {
            return (true, null, null);
        }

        if (!string.IsNullOrWhiteSpace(geometry.Crs.Type) &&
            !string.Equals(geometry.Crs.Type, CrsTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, "CRS type must be 'name'.");
        }

        var crsName = geometry.Crs.Properties?.Name;
        if (string.IsNullOrWhiteSpace(crsName))
        {
            return (false, null, "CRS name is required.");
        }

        var definition = await crsRegistry.ResolveAsync(crsName, cancellationToken).ConfigureAwait(false);
        if (!definition.HasValue)
        {
            return (false, null, $"Unsupported geometry CRS '{crsName}'.");
        }

        if (requiredSrid.HasValue && requiredSrid.Value > 0 && definition.Value.Srid != requiredSrid.Value)
        {
            return (false, null, $"Geometry CRS SRID {definition.Value.Srid} does not match layer SRID {requiredSrid.Value}.");
        }

        if (definition.Value.Srid == SpatialReference.WGS84.Wkid)
        {
            definition = definition.Value with { AxisOrder = AxisOrder.EastNorth };
        }

        return (true, definition, null);
    }
}
