// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Snowflake.Features.FeatureStore;
using Honua.Snowflake.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Snowflake.Tests;

/// <summary>
/// Integration tests against a live Snowflake account. Gated behind <c>HONUA_TEST_SNOWFLAKE=1</c>
/// plus <c>HONUA_SNOWFLAKE_TEST_CONNECTION</c>; they report an explicit credential skip in normal CI so the suite stays
/// hermetic. Configure a Snowflake account with a table whose schema matches the test publication
/// below (<c>ANALYTICS.PUBLIC.PARCELS</c> with an OBJECTID key and a GEOGRAPHY column named SHAPE).
/// </summary>
[Trait("Category", "Snowflake")]
public sealed class SnowflakeFeatureStoreIntegrationTests
{
    private const int LayerId = 1;
    private const string EnableEnvVar = "HONUA_TEST_SNOWFLAKE";
    private const string ConnectionEnvVar = "HONUA_SNOWFLAKE_TEST_CONNECTION";

    [RequiredEnvironmentVariablesFact(EnableEnvVar, ConnectionEnvVar)]
    public async Task Count_OnLiveSnowflakeTable_ReturnsRows()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvVar)!;
        var reader = CreateBoundReader(connectionString);

        var count = await reader.CountAsync(LayerId, new FeatureQuery());

        Assert.True(count > 0, "The configured live fixture must contain at least one row.");
    }

    [RequiredEnvironmentVariablesFact(EnableEnvVar, ConnectionEnvVar)]
    public async Task Query_OnLiveSnowflakeTable_ReturnsFeaturesWithGeometry()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvVar)!;
        var reader = CreateBoundReader(connectionString);

        var result = await reader.QueryAsync(LayerId, new FeatureQuery { Limit = 1 });

        var feature = Assert.Single(result.Features);
        Assert.NotNull(feature.Geometry);
        Assert.NotEmpty(feature.Geometry);
    }

    private static IFeatureReader CreateBoundReader(string connectionString)
    {
        var options = Options.Create(new SnowflakeOptions { ConnectionString = connectionString });
        var connectionFactory = new SnowflakeConnectionFactory(options);
        var dataAccess = new SnowflakeFeatureDataAccess(
            connectionFactory,
            options,
            NullLogger<SnowflakeFeatureDataAccess>.Instance);
        var provider = new SnowflakeFeatureStore(dataAccess);

        var (snapshot, service, resource, publication) = CreateSnapshot();
        var storageBinding = snapshot.ResolveStorageBinding(publication)
            ?? throw new InvalidOperationException("Test snapshot did not include a storage binding.");

        var binding = new FeatureProviderBinding(
            service,
            resource,
            publication,
            storageBinding,
            FeatureStorageMapping.FromMetadata(resource, storageBinding),
            LayerId,
            provider,
            Connection: null);

        return ((IBindableFeatureDataProvider)provider).CreateReaderForBinding(binding);
    }

    private static (MetadataV2GraphSnapshot Snapshot, MetadataV2Service Service, MetadataV2Resource Resource, MetadataV2Publication Publication)
        CreateSnapshot()
    {
        var connectionId = Guid.NewGuid();
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-parcels", Name = "Parcels" },
            SpatialReference = MetadataV2SpatialReference.Wgs84
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "Parcels" },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = ["binding-parcels"],
            SchemaFields =
            [
                new MetadataV2Field { Name = "OBJECTID", Type = MetadataV2FieldType.BigInteger, Nullable = false, SemanticRoles = ["id.primary"] },
                new MetadataV2Field { Name = "SHAPE", Type = MetadataV2FieldType.Geography, Nullable = false, SemanticRoles = ["geometry.primary"] }
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Polygon,
                PrimaryGeometryField = "SHAPE"
            }
        };
        var storageBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-parcels", Name = "binding-parcels" },
            ResourceId = resource.Metadata.Id,
            ConnectionId = connectionId.ToString(),
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "ANALYTICS.PUBLIC.PARCELS",
            StorageLayerId = LayerId
        };
        var metadataConnection = new MetadataV2Connection
        {
            Metadata = new MetadataV2ObjectMetadata { Id = connectionId.ToString(), Name = "secure" },
            Provider = DataProviderNames.Snowflake
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-parcels", Name = "Parcels" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = storageBinding.Metadata.Id,
            Identifier = new MetadataV2PublicationIdentifier { Value = "1", IsNumeric = true }
        };
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Resources = [resource],
            Connections = [metadataConnection],
            StorageBindings = [storageBinding],
            Services = [service],
            Publications = [publication]
        };

        return (new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow), service, resource, publication);
    }
}
