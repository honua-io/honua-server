// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Databricks.Features.FeatureStore;
using Honua.Db.Databricks.Features.FeatureStore.Services;
using Honua.Db.Databricks.Features.Infrastructure;

namespace Honua.Db.Databricks.Tests;

/// <summary>
/// Verifies read-policy parity for the Databricks provider: a read is refused when a permanent
/// filter, row-level security predicate or field mask applies to the layer (the provider
/// applies none of them), and the emitted SQL is unchanged when nothing resolves.
/// </summary>
public class DatabricksFeatureStoreReadPolicyTests
{
    private const int LayerId = 1;

    public static TheoryData<string> ReadOperations => new() { "get", "query", "ids", "count", "extent", "estimates" };

    [Theory]
    [MemberData(nameof(ReadOperations))]
    public async Task Read_WithRowLevelSecurityPredicateResolved_IsRefusedBeforeExecuting(string operation)
    {
        var dataAccess = new RecordingDataAccess();
        var reader = CreateStore(dataAccess, CreateResolver(
            rowFilter: new Honua.Core.Queries.Filters.SqlFragment("region = @p0", ["west"])));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => InvokeReadAsync(reader, operation));

        Assert.Contains("row-level security", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Databricks", exception.Message, StringComparison.Ordinal);
        Assert.Empty(dataAccess.Statements);
    }

    [Theory]
    [MemberData(nameof(ReadOperations))]
    public async Task Read_WithFieldMaskResolved_IsRefusedBeforeExecuting(string operation)
    {
        var dataAccess = new RecordingDataAccess();
        var reader = CreateStore(dataAccess, CreateResolver(maskedFields: ["owner"]));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => InvokeReadAsync(reader, operation));

        Assert.Contains("field-mask", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Databricks", exception.Message, StringComparison.Ordinal);
        Assert.Empty(dataAccess.Statements);
    }

    [Theory]
    [MemberData(nameof(ReadOperations))]
    public async Task Read_WithPermanentFilterConfigured_IsRefusedBeforeExecuting(string operation)
    {
        var dataAccess = new RecordingDataAccess();
        var reader = CreateStore(dataAccess, CreateResolver(permanentFilterExpression: "status = 'active'"));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => InvokeReadAsync(reader, operation));

        Assert.Contains("permanent", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Databricks", exception.Message, StringComparison.Ordinal);
        Assert.Empty(dataAccess.Statements);
    }

    [Theory]
    [MemberData(nameof(ReadOperations))]
    public async Task Read_WithNoPolicyResolved_EmitsSameSqlAsWithoutResolver(string operation)
    {
        var baseline = new RecordingDataAccess();
        await InvokeReadAsync(CreateStore(baseline, readSecurity: null), operation);

        var withResolver = new RecordingDataAccess();
        await InvokeReadAsync(CreateStore(withResolver, CreateResolver()), operation);

        Assert.NotEmpty(baseline.Statements);
        Assert.Equal(baseline.Statements, withResolver.Statements);
    }

    private static LayerReadSecurityResolver CreateResolver(
        Honua.Core.Queries.Filters.SqlFragment? rowFilter = null,
        string[]? maskedFields = null,
        string? permanentFilterExpression = null)
        => new(
            new StubV2Provider(LayerId, permanentFilterExpression),
            filterExpressionService: null,
            new StubRowFilterSource(rowFilter),
            new StubFieldMaskSource(maskedFields ?? []));

    private static DatabricksFeatureStore CreateStore(RecordingDataAccess dataAccess, LayerReadSecurityResolver? readSecurity)
    {
        var mapping = new DatabricksLayerMapping
        {
            LayerId = LayerId,
            Table = "parcels",
            Catalog = "main",
            Schema = "gis",
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            GeometryType = Honua.Core.Features.Catalog.Domain.GeometryType.Polygon,
            AttributeColumns = ["name", "owner"],
        };

        return new DatabricksFeatureStore(
            new DatabricksLayerMappingRegistry([mapping]),
            new DatabricksFeatureQueryBuilder(),
            dataAccess,
            readSecurity);
    }

    private static Task InvokeReadAsync(DatabricksFeatureStore reader, string operation) => operation switch
    {
        "get" => reader.GetAsync(LayerId, 1),
        "query" => reader.QueryAsync(LayerId, new FeatureQuery { Where = "name = 'a'", Limit = 10 }),
        "ids" => reader.QueryObjectIdsAsync(LayerId, new FeatureQuery { Where = "name = 'a'" }),
        "count" => reader.CountAsync(LayerId, new FeatureQuery { Where = "name = 'a'" }),
        "extent" => reader.GetExtentAsync(LayerId),
        "estimates" => reader.GetEstimatesAsync(LayerId),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };

    private sealed class StubV2Provider(int storageLayerId, string? permanentFilterExpression) :
        Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            var resource = new MetadataV2Resource
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "Parcels" },
                Type = MetadataV2ResourceType.FeatureDataset,
                PermanentFilter = permanentFilterExpression == null ? null : new MetadataV2PermanentFilter
                {
                    Expression = permanentFilterExpression,
                    Language = MetadataV2PermanentFilterLanguages.ArcGisSql
                }
            };
            var storageBinding = new MetadataV2StorageBinding
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "binding-parcels", Name = "binding-parcels" },
                ResourceId = resource.Metadata.Id,
                StorageLayerId = storageLayerId,
                StorageType = MetadataV2StorageType.RelationalTable,
                Locator = "gis.parcels"
            };
            var graph = new MetadataV2Graph
            {
                Revision = 1,
                Environment = "test",
                Resources = [resource],
                StorageBindings = [storageBinding]
            };
            return ValueTask.FromResult(new MetadataV2GraphSnapshot(graph, "etag-stub", DateTimeOffset.UtcNow));
        }

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(null);
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

    private sealed class RecordingDataAccess : IDatabricksFeatureDataAccess
    {
        public List<string> Statements { get; } = [];

        public Task<ImmutableArray<Feature>> ExecuteSelectAsync(
            DatabricksLayerMapping mapping, DatabricksSqlStatement statement, CancellationToken cancellationToken)
            => Record(statement, ImmutableArray<Feature>.Empty);

        public Task<long> ExecuteCountAsync(DatabricksSqlStatement statement, CancellationToken cancellationToken)
            => Record(statement, 0L);

        public Task<FeatureExtent?> ExecuteExtentAsync(
            DatabricksLayerMapping mapping, DatabricksSqlStatement statement, CancellationToken cancellationToken)
            => Record<FeatureExtent?>(statement, null);

        public Task<ImmutableArray<long>> ExecuteObjectIdsAsync(DatabricksSqlStatement statement, CancellationToken cancellationToken)
            => Record(statement, ImmutableArray<long>.Empty);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsAsync(
            DatabricksSqlStatement statement, CancellationToken cancellationToken)
            => Record(statement, ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);

        private Task<T> Record<T>(DatabricksSqlStatement statement, T result)
        {
            lock (Statements)
            {
                Statements.Add(statement.Sql + " | " + string.Join(",", statement.Parameters.Select(static p => $"{p.Name}={p.Value}")));
                Statements.Sort(StringComparer.Ordinal);
            }

            return Task.FromResult(result);
        }
    }
}
