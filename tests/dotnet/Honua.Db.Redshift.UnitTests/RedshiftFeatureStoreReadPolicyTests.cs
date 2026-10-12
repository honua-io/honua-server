// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Redshift.Features.FeatureStore;
using Honua.Db.Redshift.Features.FeatureStore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Db.Redshift.Tests;

/// <summary>
/// Verifies read-policy parity for the Redshift provider: a bound reader refuses a read when a
/// permanent filter, row-level security predicate or field mask applies to the layer (the
/// provider applies none of them), and reads exactly as before when nothing resolves.
/// </summary>
public class RedshiftFeatureStoreReadPolicyTests
{
    private const int LayerId = 1;

    public static TheoryData<string> ReadOperations => new() { "get", "query", "ids", "count", "extent", "estimates" };

    [Theory]
    [MemberData(nameof(ReadOperations))]
    public async Task BoundReader_WithRowLevelSecurityPredicateResolved_RefusesReadBeforeConnecting(string operation)
    {
        var factory = new RecordingConnectionFactory();
        var readSecurity = new LayerReadSecurityResolver(
            v2Provider: null,
            filterExpressionService: null,
            new StubRowFilterSource(new Honua.Core.Queries.Filters.SqlFragment("region = @p0", ["west"])),
            new StubFieldMaskSource([]));
        var reader = CreateBoundReader(factory, readSecurity);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => InvokeReadAsync(reader, operation));

        Assert.Contains("row-level security", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Redshift", exception.Message, StringComparison.Ordinal);
        Assert.False(factory.WasCalled);
    }

    [Theory]
    [MemberData(nameof(ReadOperations))]
    public async Task BoundReader_WithFieldMaskResolved_RefusesReadBeforeConnecting(string operation)
    {
        var factory = new RecordingConnectionFactory();
        var readSecurity = new LayerReadSecurityResolver(
            v2Provider: null,
            filterExpressionService: null,
            new StubRowFilterSource(null),
            new StubFieldMaskSource(["name"]));
        var reader = CreateBoundReader(factory, readSecurity);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => InvokeReadAsync(reader, operation));

        Assert.Contains("field-mask", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Redshift", exception.Message, StringComparison.Ordinal);
        Assert.False(factory.WasCalled);
    }

    [Theory]
    [MemberData(nameof(ReadOperations))]
    public async Task BoundReader_WithPermanentFilterConfigured_RefusesReadBeforeConnecting(string operation)
    {
        var factory = new RecordingConnectionFactory();
        var readSecurity = new LayerReadSecurityResolver(
            v2Provider: null,
            filterExpressionService: null,
            new StubRowFilterSource(null),
            new StubFieldMaskSource([]));
        var reader = CreateBoundReader(factory, readSecurity, permanentFilterExpression: "status = 'active'");

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => InvokeReadAsync(reader, operation));

        Assert.Contains("permanent", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Redshift", exception.Message, StringComparison.Ordinal);
        Assert.False(factory.WasCalled);
    }

    [Theory]
    [MemberData(nameof(ReadOperations))]
    public async Task BoundReader_WithNoPolicyResolved_ReadsAsBefore(string operation)
    {
        var factory = new RecordingConnectionFactory();
        var rowSource = new StubRowFilterSource(null);
        var maskSource = new StubFieldMaskSource([]);
        var readSecurity = new LayerReadSecurityResolver(v2Provider: null, filterExpressionService: null, rowSource, maskSource);
        var reader = CreateBoundReader(factory, readSecurity);

        // No policy resolves, so the read proceeds to the connection factory exactly as it
        // does without the resolver.
        await Assert.ThrowsAsync<RecordingConnectionFactory.SentinelException>(() => InvokeReadAsync(reader, operation));

        Assert.True(factory.WasCalled);
        Assert.Equal("res-parcels", rowSource.LastResourceId);
        Assert.Equal("res-parcels", maskSource.LastResourceId);
    }

    private static IFeatureReader CreateBoundReader(
        IRedshiftConnectionFactory factory,
        LayerReadSecurityResolver readSecurity,
        string? permanentFilterExpression = null)
    {
        var provider = CreateStore(factory, readSecurity);
        var binding = CreateBinding(provider, connection: null);
        if (permanentFilterExpression is not null)
        {
            binding = binding with
            {
                Resource = binding.Resource with
                {
                    PermanentFilter = new MetadataV2PermanentFilter
                    {
                        Expression = permanentFilterExpression,
                        Language = MetadataV2PermanentFilterLanguages.ArcGisSql
                    }
                }
            };
        }

        return ((IBindableFeatureDataProvider)provider).CreateReaderForBinding(binding);
    }

    private static Task InvokeReadAsync(IFeatureReader reader, string operation) => operation switch
    {
        "get" => reader.GetAsync(LayerId, 1),
        "query" => reader.QueryAsync(LayerId, new FeatureQuery()),
        "ids" => reader.QueryObjectIdsAsync(LayerId, new FeatureQuery()),
        "count" => reader.CountAsync(LayerId, new FeatureQuery()),
        "extent" => reader.GetExtentAsync(LayerId),
        "estimates" => reader.GetEstimatesAsync(LayerId),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };

    private static FeatureProviderBinding CreateBinding(RedshiftFeatureStore provider, DataConnection? connection)
    {
        var (snapshot, service, resource, publication) = CreateSnapshot(connection?.ConnectionId ?? Guid.NewGuid());
        var storageBinding = snapshot.ResolveStorageBinding(publication)
            ?? throw new InvalidOperationException("Test snapshot did not include a storage binding.");

        return new FeatureProviderBinding(
            service,
            resource,
            publication,
            storageBinding,
            FeatureStorageMapping.FromMetadata(resource, storageBinding),
            LayerId,
            provider,
            connection);
    }

    private static (MetadataV2GraphSnapshot Snapshot, MetadataV2Service Service, MetadataV2Resource Resource, MetadataV2Publication Publication)
        CreateSnapshot(Guid connectionId, string providerAlias = DataProviderNames.Redshift)
    {
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
                new MetadataV2Field
                {
                    Name = "OBJECTID",
                    Type = MetadataV2FieldType.BigInteger,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field
                {
                    Name = "SHAPE",
                    Type = MetadataV2FieldType.Geography,
                    Nullable = false,
                    SemanticRoles = ["geometry.primary"]
                },
                new MetadataV2Field { Name = "NAME", Type = MetadataV2FieldType.String }
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
            Locator = "public.parcels",
            StorageLayerId = LayerId
        };
        var metadataConnection = new MetadataV2Connection
        {
            Metadata = new MetadataV2ObjectMetadata { Id = connectionId.ToString(), Name = "secure" },
            Provider = providerAlias
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-parcels", Name = "Parcels" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = storageBinding.Metadata.Id,
            Identifier = new MetadataV2PublicationIdentifier { Value = LayerId.ToString(CultureInfo.InvariantCulture), IsNumeric = true }
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

    private static RedshiftFeatureStore CreateStore(
        IRedshiftConnectionFactory factory,
        LayerReadSecurityResolver? readSecurity)
    {
        var dataAccess = new RedshiftFeatureDataAccess(
            factory,
            Options.Create(new RedshiftOptions()),
            NullLogger<RedshiftFeatureDataAccess>.Instance);

        return new RedshiftFeatureStore(dataAccess, readSecurity);
    }

    private sealed class StubRowFilterSource(Honua.Core.Queries.Filters.SqlFragment? fragment) :
        Honua.Core.Features.Authorization.Abstractions.IRowLevelSecurityFilterSource
    {
        public string? LastResourceId { get; private set; }

        public Task<Honua.Core.Queries.Filters.SqlFragment?> ResolveAsync(MetadataV2Resource resource, CancellationToken cancellationToken = default)
        {
            LastResourceId = resource.Metadata.Id;
            return Task.FromResult(fragment);
        }
    }

    private sealed class StubFieldMaskSource(string[] maskedFields) :
        Honua.Core.Features.Authorization.Abstractions.IFieldMaskSource
    {
        public string? LastResourceId { get; private set; }

        public Task<System.Collections.Immutable.ImmutableArray<string>> ResolveAsync(
            MetadataV2Resource resource, CancellationToken cancellationToken = default)
        {
            LastResourceId = resource.Metadata.Id;
            return Task.FromResult(System.Collections.Immutable.ImmutableArray.Create(maskedFields));
        }
    }

    private sealed class RecordingConnectionFactory : IRedshiftConnectionFactory
    {
        public bool WasCalled { get; private set; }

        public Task<NpgsqlConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new SentinelException();
        }

        public sealed class SentinelException : Exception
        {
        }
    }
}
