// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public static class DataProviderNames
{
    public const string Postgis = "postgis";

    public const string PostgreSql = "postgresql";

    public const string SqlServer = "sqlserver";

    public const string MySql = "mysql";

    public const string DuckDb = "duckdb";

    public const string GeoServicesRest = "arcgis-rest";

    public const string Oracle = "oracle";

    public const string Redshift = "redshift";

    public const string Snowflake = "snowflake";

    public const string Databricks = "databricks";

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
            "arcgisrest" => GeoServicesRest,
            "arcgis-rest" => GeoServicesRest,
            "arcgis" => GeoServicesRest,
            "esri" => GeoServicesRest,
            "esri-rest" => GeoServicesRest,
            "esrirest" => GeoServicesRest,
            "esri-featureserver" => GeoServicesRest,
            "esrifeatureserver" => GeoServicesRest,
            "featureserver" => GeoServicesRest,
            "oracle" => Oracle,
            "oracledb" => Oracle,
            "redshift" => Redshift,
            "amazon-redshift" => Redshift,
            "amazonredshift" => Redshift,
            "redshiftdb" => Redshift,
            "aws-redshift" => Redshift,
            "awsredshift" => Redshift,
            "snowflake" => Snowflake,
            "snowflakedb" => Snowflake,
            "databricks" => Databricks,
            "databrickssql" => Databricks,
            "dbsql" => Databricks,
            _ => normalized
        };
    }
}
