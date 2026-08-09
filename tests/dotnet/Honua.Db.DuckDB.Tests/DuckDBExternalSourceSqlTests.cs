// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.DuckDB.Features.Infrastructure;

namespace Honua.DuckDB.Tests;

public sealed class DuckDBExternalSourceSqlTests
{
    [Fact]
    public void BuildCreateTempViewSql_WithS3GeoParquet_GeneratesReadParquetView()
    {
        var layer = new DuckDBLayerOptions
        {
            Id = 4,
            Table = "parcels",
            ExternalSource = new DuckDBExternalSourceOptions
            {
                Format = "GeoParquet",
                Path = "s3://county-data/parcels/*.parquet",
                UnionByName = true
            }
        };

        var sql = DuckDBExternalSourceSql.BuildCreateTempViewSql(layer);

        Assert.Equal(
            "CREATE OR REPLACE TEMP VIEW \"parcels\" AS SELECT * FROM read_parquet('s3://county-data/parcels/*.parquet', union_by_name = true)",
            sql);
    }

    [Fact]
    public void BuildReadParquetExpression_WithMultiplePaths_EscapesLiteralsAndAddsOptions()
    {
        var source = new DuckDBExternalSourceOptions
        {
            Format = "Parquet",
            Paths =
            [
                "/data/parcels/a.parquet",
                "/data/parcels/owner''s.parquet"
            ],
            HivePartitioning = true,
            UnionByName = true
        };

        var expression = DuckDBExternalSourceSql.BuildReadParquetExpression(source);

        Assert.Equal(
            "read_parquet(['/data/parcels/a.parquet', '/data/parcels/owner''''s.parquet'], hive_partitioning = true, union_by_name = true)",
            expression);
    }

    [Fact]
    public void GetRequiredExtensions_WithRemoteSources_ReturnsHttpFsAndAzure()
    {
        var options = new DuckDBOptions
        {
            Layers =
            [
                new DuckDBLayerOptions
                {
                    Id = 1,
                    Table = "s3_parcels",
                    ExternalSource = new DuckDBExternalSourceOptions
                    {
                        Path = "s3://bucket/parcels.parquet"
                    }
                },
                new DuckDBLayerOptions
                {
                    Id = 2,
                    Table = "azure_roads",
                    ExternalSource = new DuckDBExternalSourceOptions
                    {
                        Path = "az://container/roads.parquet"
                    }
                }
            ]
        };

        var extensions = DuckDBExternalSourceSql.GetRequiredExtensions(options);

        Assert.Contains("httpfs", extensions);
        Assert.Contains("azure", extensions);
        Assert.Equal(2, extensions.Length);
    }

    [Fact]
    public void BuildConnectionSettingCommands_WithSecrets_KeepsDescriptionsSanitized()
    {
        var options = new DuckDBOptions
        {
            HttpFs = new DuckDBHttpFsOptions
            {
                S3 = new DuckDBS3Options
                {
                    Region = "us-east-1",
                    SecretAccessKey = "super-secret",
                    UrlStyle = "path",
                    UseSsl = false
                }
            },
            Azure = new DuckDBAzureOptions
            {
                ConnectionString = "DefaultEndpointsProtocol=https;AccountKey=secret"
            }
        };

        var commands = DuckDBExternalSourceSql.BuildConnectionSettingCommands(options);

        Assert.Contains(commands, command => command.Sql == "SET s3_region = 'us-east-1'");
        Assert.Contains(commands, command => command.Sql == "SET s3_secret_access_key = 'super-secret'");
        Assert.Contains(commands, command => command.Sql == "SET s3_url_style = 'path'");
        Assert.Contains(commands, command => command.Sql == "SET s3_use_ssl = false");
        Assert.Contains(commands, command => command.Sql == "SET azure_storage_connection_string = 'DefaultEndpointsProtocol=https;AccountKey=secret'");
        Assert.DoesNotContain(commands, command => command.Description.Contains("super-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command => command.Description.Contains("AccountKey=secret", StringComparison.Ordinal));
    }
}
