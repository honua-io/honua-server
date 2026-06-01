// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Canonical provider names used when binding secure connections to feature-store providers.
/// </summary>
public static class DataProviderNames
{
    /// <summary>
    /// Canonical PostGIS provider name.
    /// </summary>
    public const string Postgis = "postgis";

    /// <summary>
    /// Canonical PostgreSQL provider name for non-PostGIS storage.
    /// </summary>
    public const string PostgreSql = "postgresql";

    /// <summary>
    /// Canonical SQL Server provider name.
    /// </summary>
    public const string SqlServer = "sqlserver";

    /// <summary>
    /// Canonical MySQL provider name.
    /// </summary>
    public const string MySql = "mysql";

    /// <summary>
    /// Canonical DuckDB provider name.
    /// </summary>
    public const string DuckDb = "duckdb";

    /// <summary>
    /// Canonical ArcGIS REST FeatureServer/MapServer read-through provider name.
    /// Bound to secure-connection records whose connection-string field holds a
    /// remote ArcGIS service root URL.
    /// </summary>
    public const string ArcGisRest = "arcgis-rest";

    /// <summary>
    /// Canonical Oracle provider name.
    /// </summary>
    public const string Oracle = "oracle";

    /// <summary>
    /// Normalizes a provider name or alias into the canonical provider identifier.
    /// Unknown non-empty provider names are lower-cased so external provider registrations
    /// can participate without changing the core model.
    /// </summary>
    /// <param name="providerName">Provider name or alias.</param>
    /// <returns>Canonical provider identifier.</returns>
    public static string Normalize(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return Postgis;
        }

        var normalized = providerName.Trim().ToLowerInvariant().Replace("_", string.Empty, StringComparison.Ordinal);

        return normalized switch
        {
            "postgres" => Postgis,
            "postgresql" => PostgreSql,
            "postgis" => Postgis,
            "pgsql" => PostgreSql,
            "sqlserver" => SqlServer,
            "mssql" => SqlServer,
            "mysql" => MySql,
            "mariadb" => MySql,
            "duckdb" => DuckDb,
            "arcgisrest" => ArcGisRest,
            "arcgis-rest" => ArcGisRest,
            "arcgis" => ArcGisRest,
            "esri" => ArcGisRest,
            "esri-rest" => ArcGisRest,
            "esrirest" => ArcGisRest,
            "esri-featureserver" => ArcGisRest,
            "esrifeatureserver" => ArcGisRest,
            "featureserver" => ArcGisRest,
            "oracle" => Oracle,
            "oracledb" => Oracle,
            _ => normalized
        };
    }
}
