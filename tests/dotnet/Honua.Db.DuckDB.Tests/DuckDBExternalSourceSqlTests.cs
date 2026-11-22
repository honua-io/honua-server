// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using DuckDB.NET.Data;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.ObjectStorage;
using Honua.Core.Features.Security;
using Honua.Core.Queries.Filters;
using Honua.Db.DuckDB.Features.FeatureStore;
using Honua.Db.DuckDB.Features.FeatureStore.Services;
using Honua.Db.DuckDB.Features.Infrastructure;
using Honua.Db.DuckDB.Queries.Filters;
using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task ManagedSource_UsesBoundObjectAndReusesSnapshotAcrossConnections()
    {
        var connection = new ObjectStorageConnection
        {
            Id = Guid.NewGuid(),
            Name = "county-bucket",
            Bucket = "county-geoparquet",
            Prefix = "analytics/",
            Region = "us-west-2",
            ExternalId = "test-external-id",
        };
        var reader = new StubObjectStoreReader(await CreateParquetAsync());
        using var bootstrap = new DuckDBSpatialBootstrap(null, NullLogger<DuckDBSpatialBootstrap>.Instance);
        using var source = new DuckDBManagedSource(
            bootstrap, NullLogger<DuckDBConnectionProvider>.Instance, new StubObjectStorageConnectionResolver(connection), reader);
        var provider = source.CreateConnectionProvider(ManagedMapping(connection.Id, "analytics/parcels.parquet"));

        for (var i = 0; i < 2; i++)
        {
            await using var db = await provider.OpenConnectionAsync();
            await using var query = db.CreateCommand();
            query.CommandText = "SELECT count(*) FROM parcels";
            Assert.Equal(3L, await query.ExecuteScalarAsync());

            query.CommandText = "SELECT ST_AsText(geom) FROM parcels WHERE id = 1";
            Assert.Equal("POINT (1 0)", await query.ExecuteScalarAsync());
        }

        Assert.Equal(connection, reader.Location?.Connection);
        Assert.Equal("analytics/parcels.parquet", reader.Location?.Key);
        Assert.Equal("test-etag", reader.ETag);
        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public async Task ManagedSource_RejectsKeyOutsideResolvedPrefixBeforeReading()
    {
        var connection = new ObjectStorageConnection
        {
            Id = Guid.NewGuid(),
            Name = "county-bucket",
            Bucket = "county-geoparquet",
            Prefix = "analytics/",
            Region = "us-west-2",
        };
        var reader = new StubObjectStoreReader([]);
        using var bootstrap = new DuckDBSpatialBootstrap(null, NullLogger<DuckDBSpatialBootstrap>.Instance);
        using var source = new DuckDBManagedSource(
            bootstrap, NullLogger<DuckDBConnectionProvider>.Instance, new StubObjectStorageConnectionResolver(connection), reader);
        var provider = source.CreateConnectionProvider(ManagedMapping(connection.Id, "outside/parcels.parquet"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenConnectionAsync());
        Assert.Null(reader.Location);
    }

    [Fact]
    public async Task ManagedSource_RetranslatesRoutedFiltersForTheBoundProvider()
    {
        var connection = new ObjectStorageConnection
        {
            Id = Guid.NewGuid(),
            Name = "county-bucket",
            Bucket = "county-geoparquet",
            Prefix = "analytics/",
            Region = "us-west-2",
        };
        var reader = new StubObjectStoreReader(await CreateParquetAsync());
        using var bootstrap = new DuckDBSpatialBootstrap(null, NullLogger<DuckDBSpatialBootstrap>.Instance);
        using var source = new DuckDBManagedSource(
            bootstrap, NullLogger<DuckDBConnectionProvider>.Instance, new StubObjectStorageConnectionResolver(connection), reader);
        var storage = ManagedStorage(connection.Id, "analytics/parcels.parquet");
        var mapping = DuckDBLayerMapping.FromStorage(5, storage, []);
        var registry = new DuckDBLayerRegistry([mapping]);
        var logger = NullLogger<DuckDBFeatureDataAccess>.Instance;
        var store = new DuckDBFeatureStore(
            new DuckDBFeatureQueryBuilder(registry),
            new DuckDBFeatureDataAccess(source.CreateConnectionProvider(mapping), registry, null, logger),
            new DuckDBFeatureCacheManager(registry),
            new LayerReadSecurityResolver(null, null, null, null),
            filterExpressionService: new FilterExpressionService(new FilterExpressionTranslator(new DuckDbSqlFilterTranslator())),
            managedSource: source,
            dataAccessLogger: logger);
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "parcels" },
            Type = MetadataV2ObjectType.FeatureDataset,
            SchemaFields = [
                new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] }
            ]
        };
        var bound = store.CreateReaderForBinding(new FeatureProviderBinding(
            null!, resource, null!, null!, storage, mapping.LayerId, store, null));

        var callerFilter = new SqlFragment("id >= @p0", [1L])
        {
            Dialect = "postgres",
            SourceExpression = new BinaryExpression(
                new PropertyReference("id"), BinaryOperator.GreaterThanOrEqual, new Literal(1L, LiteralType.Number))
        };
        var upperBound = new SqlFragment("id <= @p0", [1L])
        {
            Dialect = "postgres",
            SourceExpression = new BinaryExpression(
                new PropertyReference("id"), BinaryOperator.LessThanOrEqual, new Literal(1L, LiteralType.Number))
        };
        var result = await bound.QueryObjectIdsAsync(mapping.LayerId, new FeatureQuery
        {
            SqlFilter = SqlFragmentHelpers.CombineSqlFilters(callerFilter, upperBound),
        });

        Assert.Equal(1L, Assert.Single(result));
        await Assert.ThrowsAsync<NotSupportedException>(() => bound.QueryObjectIdsAsync(mapping.LayerId, new FeatureQuery
        {
            SqlFilter = new SqlFragment("id >= @p0", [1L]),
        }));
    }

    private static DuckDBLayerMapping ManagedMapping(Guid connectionId, string key)
        => DuckDBLayerMapping.FromStorage(5, ManagedStorage(connectionId, key), []);

    private static FeatureStorageMapping ManagedStorage(Guid connectionId, string key)
        => new(
            TableName: "parcels",
            PrimaryKeyColumn: "id",
            GeometryColumn: "geom",
            StorageSrid: 4326,
            ProviderOptions: new Dictionary<string, string>
            {
                [DuckDBProviderOptions.ExternalSourceFormat] = "GeoParquet",
                [DuckDBProviderOptions.ObjectStorageConnectionId] = connectionId.ToString(),
                [DuckDBProviderOptions.ObjectStorageKey] = key,
            });

    private static async Task<byte[]> CreateParquetAsync()
    {
        var path = PrivateTempFile.NewPath("honua-duckdb-test-", ".parquet");
        try
        {
            await using var db = new DuckDBConnection("Data Source=:memory:");
            await db.OpenAsync();
            await using var query = db.CreateCommand();
            query.CommandText = "INSTALL spatial";
            await query.ExecuteNonQueryAsync();
            query.CommandText = "LOAD spatial";
            await query.ExecuteNonQueryAsync();
            query.CommandText = $"COPY (SELECT range AS id, ST_Point(range, 0) AS geom FROM range(3)) TO {DuckDBExternalSourceSql.QuoteLiteral(path)} (FORMAT parquet)";
            await query.ExecuteNonQueryAsync();
            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class StubObjectStoreReader(byte[] data) : IImportObjectStoreReader
    {
        public ImportObjectLocation? Location { get; private set; }
        public string? ETag { get; private set; }
        public int ReadCount { get; private set; }

        public Task<ImportObjectHead> HeadAsync(ImportObjectLocation location, CancellationToken cancellationToken)
        {
            Location = location;
            return Task.FromResult(new ImportObjectHead { SizeBytes = data.Length, ETag = "test-etag" });
        }

        public Task<Stream> OpenReadAsync(ImportObjectLocation location, string etag, CancellationToken cancellationToken)
        {
            Assert.Equal(Location, location);
            ETag = etag;
            ReadCount++;
            return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
        }
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
