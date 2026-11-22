// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.ObjectStorage;
using Honua.Db.DuckDB.Features.Infrastructure;

namespace Honua.Db.DuckDB.Tests;

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

        Assert.Contains(commands, command => command.Sql == "SET GLOBAL s3_region = 'us-east-1'");
        Assert.Contains(commands, command => command.Sql == "SET GLOBAL s3_secret_access_key = 'super-secret'");
        Assert.Contains(commands, command => command.Sql == "SET GLOBAL s3_url_style = 'path'");
        Assert.Contains(commands, command => command.Sql == "SET GLOBAL s3_use_ssl = false");
        Assert.Contains(commands, command => command.Sql == "SET GLOBAL azure_storage_connection_string = 'DefaultEndpointsProtocol=https;AccountKey=secret'");
        Assert.DoesNotContain(commands, command => command.Description.Contains("super-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command => command.Description.Contains("AccountKey=secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("s3://county-data/parcels.parquet'; DROP TABLE parcels; --")]
    [InlineData("s3://county-data/../secret/parcels.parquet")]
    [InlineData("s3://user:pass@county-data/parcels.parquet")]
    [InlineData("s3://INVALID_BUCKET/parcels.parquet")]
    [InlineData("s3://county-data/")]
    [InlineData("/vsicurl/https://example.com/parcels.parquet")]
    [InlineData("s3://county-data/parcels.parquet\nSELECT 1")]
    public void BuildReadParquetExpression_WithInjectionOrUnsafeUri_ThrowsArgumentException(string unsafePath)
    {
        var source = new DuckDBExternalSourceOptions
        {
            Format = "GeoParquet",
            Path = unsafePath,
        };

        Assert.Throws<ArgumentException>(() => DuckDBExternalSourceSql.BuildReadParquetExpression(source));
    }

    [Fact]
    public void BuildExternalSourcePlans_WithS3GeoParquet_RequiresSpatialAndHttpFsExtensions()
    {
        var plans = DuckDBExternalSourceSql.BuildExternalSourcePlans(
        [
            new DuckDBLayerOptions
            {
                Id = 10,
                Table = "s3_parcels",
                ExternalSource = new DuckDBExternalSourceOptions
                {
                    Format = "GeoParquet",
                    Path = "s3://county-data/parcels/2026/*.parquet",
                    BboxColumn = "bbox",
                },
            }
        ]);

        var plan = Assert.Single(plans);
        Assert.Contains("spatial", plan.RequiredExtensions);
        Assert.Contains("httpfs", plan.RequiredExtensions);
        Assert.Contains("read_parquet('s3://county-data/parcels/2026/*.parquet')", plan.CreateViewSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveObjectStorageBindingAsync_WithValidConnectionAndKey_BuildsCanonicalS3UriWithoutLeakingExternalId()
    {
        var connectionId = Guid.NewGuid();
        var resolver = new StubObjectStorageConnectionResolver(new ObjectStorageConnection
        {
            Id = connectionId,
            Name = "county-bucket",
            Bucket = "county-geoparquet",
            Prefix = "analytics/",
            Region = "us-west-2",
            ExternalId = "secret-external-id-must-not-leak",
        });

        var resolved = await DuckDBExternalSourceSql.ResolveObjectStorageBindingAsync(
            resolver,
            connectionId,
            ["analytics/parcels.parquet"]);

        Assert.Equal("county-geoparquet", resolved.Bucket);
        Assert.Equal("us-west-2", resolved.Region);
        Assert.Equal("s3://county-geoparquet/analytics/parcels.parquet", Assert.Single(resolved.SourceUris));
        Assert.Contains("spatial", resolved.RequiredExtensions);
        Assert.Contains("httpfs", resolved.RequiredExtensions);
        Assert.Contains(resolved.ConnectionSettings, c => c.Sql == "SET GLOBAL s3_region = 'us-west-2'");
        Assert.DoesNotContain(resolved.ConnectionSettings, c => c.Sql.Contains("secret-external-id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveObjectStorageBindingAsync_WithKeyOutsidePrefix_ThrowsInvalidOperationException()
    {
        var connectionId = Guid.NewGuid();
        var resolver = new StubObjectStorageConnectionResolver(new ObjectStorageConnection
        {
            Id = connectionId,
            Name = "county-bucket",
            Bucket = "county-geoparquet",
            Prefix = "analytics/",
            Region = "us-west-2",
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DuckDBExternalSourceSql.ResolveObjectStorageBindingAsync(
                resolver,
                connectionId,
                ["other/parcels.parquet"]));
    }

    [Fact]
    public void LayerMappingFromStorage_WithS3ExternalSourceAndBboxColumn_ValidatesAndBindsMetadata()
    {
        var storage = new FeatureStorageMapping(
            TableName: "s3_parcels",
            PrimaryKeyColumn: "id",
            GeometryColumn: "geom",
            StorageSrid: 4326,
            ProviderOptions: new Dictionary<string, string>
            {
                [DuckDBProviderOptions.ExternalSourcePath] = "s3://county-geoparquet/analytics/parcels.parquet",
                [DuckDBProviderOptions.BboxColumn] = "bbox",
            });

        var mapping = DuckDBLayerMapping.FromStorage(5, storage, ["name"]);

        Assert.True(mapping.IsExternalSource);
        Assert.True(mapping.SupportsBboxPushdown);
        Assert.Equal("\"bbox\"", mapping.QuotedBboxColumn);
        Assert.Equal("s3://county-geoparquet/analytics/parcels.parquet", Assert.Single(mapping.ExternalSourcePaths));
    }

    private sealed class StubObjectStorageConnectionResolver(ObjectStorageConnection connection) : IObjectStorageConnectionResolver
    {
        public Task<IReadOnlyList<ObjectStorageConnection>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ObjectStorageConnection>>([connection]);

        public Task<ObjectStorageConnection?> FindAsync(Guid connectionId, CancellationToken cancellationToken)
            => Task.FromResult(connectionId == connection.Id ? connection : null);

        public Task<ObjectStorageResolution> ResolveObjectAsync(Guid connectionId, string? key, CancellationToken cancellationToken)
        {
            if (connectionId != connection.Id)
            {
                return Task.FromResult(new ObjectStorageResolution
                {
                    Failure = ObjectStorageResolveFailure.ConnectionNotFound,
                    Message = "Connection not found.",
                });
            }

            if (!ObjectStoragePolicy.IsKeyUnderPrefix(connection.Prefix, key))
            {
                return Task.FromResult(new ObjectStorageResolution
                {
                    Connection = connection,
                    Failure = ObjectStorageResolveFailure.InvalidKey,
                    Message = "Key lies outside the connection prefix.",
                });
            }

            return Task.FromResult(new ObjectStorageResolution
            {
                Connection = connection,
                Key = key,
            });
        }
    }
}
