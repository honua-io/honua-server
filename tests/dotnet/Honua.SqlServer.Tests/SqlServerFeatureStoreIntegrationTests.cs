// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.SqlServer;
using Honua.SqlServer.Features.FeatureStore;
using Honua.SqlServer.Features.FeatureStore.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.SqlServer.Tests;

/// <summary>
/// Gated integration tests for the SQL Server feature provider.
/// </summary>
/// <remarks>
/// <para>These tests run against a real SQL Server instance and are skipped automatically
/// when the <c>HONUA_SQLSERVER_TEST_CONNECTION</c> environment variable is not set. The
/// connection string must point to a SQL Server 2016+ instance where the test user can
/// create and drop a temporary table.</para>
/// <para>Manual run example:</para>
/// <code>
///   export HONUA_SQLSERVER_TEST_CONNECTION="Server=localhost,1433;Database=tempdb;User Id=sa;Password=Strong!Pass;TrustServerCertificate=True"
///   dotnet test tests/dotnet/Honua.SqlServer.Tests --filter Category=SqlServerIntegration
/// </code>
/// </remarks>
[Trait("Category", "SqlServerIntegration")]
public class SqlServerFeatureStoreIntegrationTests : IAsyncLifetime
{
    private const string ConnectionEnvVar = "HONUA_SQLSERVER_TEST_CONNECTION";
    private const int LayerId = 1;

    private string? _connectionString;
    private string _tableName = null!;
    private IFeatureReader _store = null!;

    public static bool ShouldRun => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar));

    public async Task InitializeAsync()
    {
        _connectionString = Environment.GetEnvironmentVariable(ConnectionEnvVar);
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        _tableName = $"honua_sqlserver_test_{Guid.NewGuid():N}";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await Execute(connection, $"""
            CREATE TABLE [dbo].[{_tableName}] (
                [objectid] bigint NOT NULL PRIMARY KEY,
                [name] nvarchar(100) NULL,
                [shape] geometry NULL
            )
            """);

        await Execute(connection, $"""
            INSERT INTO [dbo].[{_tableName}] ([objectid], [name], [shape])
            VALUES
                (1, 'Alpha', geometry::STGeomFromText('POINT(0 0)', 4326)),
                (2, 'Bravo', geometry::STGeomFromText('POINT(10 20)', 4326)),
                (3, 'Charlie', geometry::STGeomFromText('POINT(-5 5)', 4326))
            """);

        var options = Options.Create(new SqlServerOptions { ConnectionString = _connectionString });
        var connectionFactory = new SqlServerConnectionFactory(options);
        var dataAccess = new SqlServerFeatureDataAccess(
            connectionFactory,
            options,
            NullLogger<SqlServerFeatureDataAccess>.Instance);

        var provider = new SqlServerFeatureStore(dataAccess);
        var binding = CreateBinding(provider, _tableName);
        _store = ((IBindableFeatureDataProvider)provider).CreateReaderForBinding(binding);
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_connectionString) || string.IsNullOrWhiteSpace(_tableName))
        {
            return;
        }

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            await Execute(connection, $"DROP TABLE IF EXISTS [dbo].[{_tableName}]");
        }
        catch
        {
            // Best-effort cleanup; the test database may already have been dropped.
        }
    }

    [SkippableFact]
    public async Task QueryAsync_NoFilter_ReturnsAllFeatures()
    {
        Skip.IfNot(ShouldRun, $"Set {ConnectionEnvVar} to run SQL Server integration tests.");

        var result = await _store.QueryAsync(LayerId, new FeatureQuery());

        Assert.Equal(3, result.Items.Length);
        Assert.All(result.Items, f => Assert.NotNull(f.Geometry));
    }

    [SkippableFact]
    public async Task CountAsync_NoFilter_ReturnsTotalRowCount()
    {
        Skip.IfNot(ShouldRun, $"Set {ConnectionEnvVar} to run SQL Server integration tests.");

        var count = await _store.CountAsync(LayerId, new FeatureQuery());

        Assert.Equal(3, count);
    }

    [SkippableFact]
    public async Task GetExtentAsync_ReturnsLayerEnvelope()
    {
        Skip.IfNot(ShouldRun, $"Set {ConnectionEnvVar} to run SQL Server integration tests.");

        var extent = await _store.GetExtentAsync(LayerId);

        Assert.NotNull(extent);
        Assert.Equal(-5, extent!.Value.MinX);
        Assert.Equal(0, extent.Value.MinY);
        Assert.Equal(10, extent.Value.MaxX);
        Assert.Equal(20, extent.Value.MaxY);
    }

    [SkippableFact]
    public async Task QueryAsync_AttributeFilter_ReturnsMatching()
    {
        Skip.IfNot(ShouldRun, $"Set {ConnectionEnvVar} to run SQL Server integration tests.");

        var result = await _store.QueryAsync(LayerId, new FeatureQuery { Where = "name = 'Alpha'" });

        Assert.Single(result.Items);
        Assert.Equal(1, result.Items[0].Id);
    }

    [SkippableFact]
    public async Task QueryAsync_LimitMatchingTotalRows_ReportsNoMoreResults()
    {
        Skip.IfNot(ShouldRun, $"Set {ConnectionEnvVar} to run SQL Server integration tests.");

        // Three rows are seeded; a limit equal to the row count must not falsely advertise more.
        var result = await _store.QueryAsync(LayerId, new FeatureQuery { Limit = 3 });

        Assert.Equal(3, result.Items.Length);
        Assert.False(result.HasMoreResults);
    }

    [SkippableFact]
    public async Task QueryAsync_LimitBelowTotalRows_ReportsMoreResults()
    {
        Skip.IfNot(ShouldRun, $"Set {ConnectionEnvVar} to run SQL Server integration tests.");

        var result = await _store.QueryAsync(LayerId, new FeatureQuery { Limit = 2 });

        Assert.Equal(2, result.Items.Length);
        Assert.True(result.HasMoreResults);
    }

    private static async Task Execute(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static FeatureProviderBinding CreateBinding(SqlServerFeatureStore provider, string tableName)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-test", Name = "Test" },
            SpatialReference = MetadataV2SpatialReference.Wgs84
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-test", Name = "Test" },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = ["binding-test"],
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "objectid",
                    Type = MetadataV2FieldType.BigInteger,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = false,
                    SemanticRoles = ["geometry.primary"]
                }
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape"
            }
        };
        var storageBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-test", Name = "binding-test" },
            ResourceId = resource.Metadata.Id,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = $"dbo.{tableName}",
            StorageLayerId = LayerId
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-test", Name = "Test" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = storageBinding.Metadata.Id,
            Identifier = new MetadataV2PublicationIdentifier { Value = LayerId.ToString(System.Globalization.CultureInfo.InvariantCulture), IsNumeric = true }
        };

        return new FeatureProviderBinding(
            service,
            resource,
            publication,
            storageBinding,
            FeatureStorageMapping.FromMetadata(resource, storageBinding),
            LayerId,
            provider,
            Connection: null);
    }
}

internal static class Skip
{
    public static void IfNot(bool condition, string reason)
    {
        if (!condition)
        {
            throw new SkipException(reason);
        }
    }
}

internal sealed class SkipException : Exception
{
    public SkipException(string message) : base(message) { }
}

internal sealed class SkippableFactAttribute : FactAttribute
{
    public SkippableFactAttribute()
    {
        if (!SqlServerFeatureStoreIntegrationTests.ShouldRun)
        {
            base.Skip = "Set HONUA_SQLSERVER_TEST_CONNECTION to run SQL Server integration tests.";
        }
    }
}
