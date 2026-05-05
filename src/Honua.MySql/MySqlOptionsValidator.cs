// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.MySql;

/// <summary>
/// Validates <see cref="MySqlOptions"/> at startup. Surfaces all configuration errors
/// at once via <see cref="InvalidOperationException"/> so misconfigured providers fail
/// fast rather than at the first query.
/// </summary>
internal static class MySqlOptionsValidator
{
    public static void ThrowIfInvalid(MySqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            errors.Add("MySql:ConnectionString is required.");
        }

        // Engine flavor is read once at startup and threaded into the SQL builders so
        // axis-order-aware WKB I/O matches the active engine. Reject unknown values rather
        // than silently defaulting to Mysql, which would emit a 3-arg ST_GeomFromWKB on
        // MariaDB and fail at the first spatial query.
        if (!Enum.TryParse<MySqlEngineFlavor>(options.EngineFlavor, ignoreCase: true, out _))
        {
            errors.Add(
                $"MySql:EngineFlavor '{options.EngineFlavor}' is invalid. " +
                "Expected one of: Mysql, MariaDb.");
        }

        if (options.Layers.Length == 0)
        {
            errors.Add("MySql:Layers must contain at least one layer mapping.");
        }

        var seenIds = new HashSet<int>();
        foreach (var layer in options.Layers)
        {
            if (layer.Id <= 0)
            {
                errors.Add($"MySql layer '{layer.Name}' must declare a positive Id.");
            }
            else if (!seenIds.Add(layer.Id))
            {
                errors.Add($"MySql layer Id {layer.Id} is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(layer.Name))
            {
                errors.Add($"MySql layer Id {layer.Id} must declare a non-empty Name.");
            }

            if (string.IsNullOrWhiteSpace(layer.Table))
            {
                errors.Add($"MySql layer '{layer.Name}' must declare a non-empty Table.");
            }

            if (string.IsNullOrWhiteSpace(layer.GeometryColumn))
            {
                errors.Add($"MySql layer '{layer.Name}' must declare a non-empty GeometryColumn.");
            }

            if (string.IsNullOrWhiteSpace(layer.PrimaryKeyColumn))
            {
                errors.Add($"MySql layer '{layer.Name}' must declare a non-empty PrimaryKeyColumn.");
            }

            // SRID 0 means "no spatial reference" in MySQL/MariaDB and is a legitimate
            // operator configuration — ST_Distance_Sphere accepts it (treating coordinates
            // as lon/lat on the default sphere) and the canonical X/Y WKB I/O does not need
            // an SRS tag. Negative SRIDs are not valid in MySQL — reject those.
            if (layer.Srid < 0)
            {
                errors.Add($"MySql layer '{layer.Name}' must declare a non-negative Srid (use 0 for layers without an SRS).");
            }

            // Reject typos in GeometryType so a non-point layer cannot silently fall back
            // to Point and inherit point-only behaviour (e.g. distance-filter SQL). The
            // default-initialised "Point" value parses cleanly, so omitted configuration
            // remains valid. GeometryType.None is rejected explicitly: BuildSelectQuery
            // unconditionally emits ST_AsWKB on the configured geometry column, so a
            // non-spatial layer would pass validation but fail at the first query.
            if (!Enum.TryParse<GeometryType>(layer.GeometryType, ignoreCase: true, out var parsedGeometryType) ||
                parsedGeometryType == GeometryType.None)
            {
                errors.Add(
                    $"MySql layer '{layer.Name}' has invalid GeometryType '{layer.GeometryType}'. " +
                    "Expected one of: Point, MultiPoint, LineString, MultiLineString, Polygon, " +
                    "MultiPolygon, GeometryCollection.");
            }

            if (layer.Attributes.Length == 0)
            {
                // Schema introspection is intentionally out of scope for this slice.
                errors.Add($"MySql layer '{layer.Name}' must declare its Attributes explicitly; column discovery is not implemented in this slice.");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid MySQL/MariaDB provider configuration: " +
                string.Join("; ", errors));
        }
    }
}
