// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Middleware;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Hosting.UnitTests.Features.FeatureStore;

// A layer published over a source table reads that table, so a managed write for it must be refused
// rather than stored where the layer never reads it back.
[Protocol(TestProtocols.Infrastructure)]
public sealed class SourceBackedWriteGuardFeatureWriterTests
{
    private const int SourceLayer = 7;
    private const int ManagedLayer = 8;
    private const int UnpublishedLayer = 9;

    private const string Graph = """
        {"services":[{"metadata":{"id":"svc","name":"svc"}}],
         "connections":[{"metadata":{"id":"conn"},"type":"managed"}],
         "resources":[{"metadata":{"id":"src","name":"src"},"storageBindingIds":["b-src"]},
                      {"metadata":{"id":"man","name":"man"},"storageBindingIds":["b-man"]}],
         "storageBindings":[
           {"metadata":{"id":"b-src"},"resourceId":"src","connectionId":"conn","storageType":"relational-table",
            "locator":"honua_data.imported_points","storageLayerId":7,"options":{"sourceBacked":true}},
           {"metadata":{"id":"b-man"},"resourceId":"man","storageType":"relational-table","locator":"features",
            "storageLayerId":8,"options":{"attributesColumn":"attributes","layerDiscriminatorColumn":"layer_id"}}],
         "publications":[{"metadata":{"id":"p-src"},"resourceId":"src","serviceId":"svc"},
                         {"metadata":{"id":"p-man"},"resourceId":"man","serviceId":"svc"}]}
        """;

    public static TheoryData<string> WriteKinds => ["create", "update", "delete", "batch", "receipt", "transaction"];

    [UnitTheory]
    [MemberData(nameof(WriteKinds))]
    [Operation(Operations.Create)]
    public async Task WriteToASourceBackedLayer_IsRefusedBeforeTheProvider(string kind)
    {
        var (writer, inner) = CreateWriter();

        var write = () => WriteAsync(writer, kind, SourceLayer);

        (await write.Should().ThrowAsync<ReadOnlyFeatureWriteException>())
            .Which.Message.Should().Contain("source table").And.NotContain("imported_points");
        inner.Calls.Should().Be(0, "a refused write must never reach the managed store");
    }

    [UnitTheory]
    [MemberData(nameof(WriteKinds))]
    [Operation(Operations.Create)]
    public async Task WriteToAManagedOrUnpublishedLayer_ReachesTheProvider(string kind)
    {
        var (writer, inner) = CreateWriter();

        await WriteAsync(writer, kind, ManagedLayer);
        await WriteAsync(writer, kind, UnpublishedLayer);

        inner.Calls.Should().Be(2);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void FindSourceTable_SourceBackedLayer_NamesItsTable()
        => SourceBackedWriteGuardFeatureWriter.FindSourceTable(Snapshot(JsonNode.Parse(Graph)!), SourceLayer)
            .Should().Be("honua_data.imported_points");

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task DisposedGraphProvider_FailsClosed()
    {
        var inner = new CountingWriter();
        var writer = new SourceBackedWriteGuardFeatureWriter(inner, new DisposedGraphProvider());

        var write = () => writer.CreateAsync(SourceLayer, NewFeature(0));

        await write.Should().ThrowAsync<ObjectDisposedException>();
        inner.Calls.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void SourceLayerAlsoPublishedOverManagedStorage_IsWritable()
    {
        var node = JsonNode.Parse(Graph)!;
        node["storageBindings"]![1]!["storageLayerId"] = SourceLayer;

        SourceBackedWriteGuardFeatureWriter.FindSourceTable(Snapshot(node), SourceLayer).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task HostWithoutAGraph_IsNotGuarded()
    {
        var inner = new CountingWriter();
        var writer = new SourceBackedWriteGuardFeatureWriter(inner, new GraphProvider(null));

        await writer.CreateAsync(SourceLayer, NewFeature(0));

        inner.Calls.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public void Registration_WrapsTheProviderWriterAsTheOutermostDecorator()
    {
        var services = new ServiceCollection();
        services.AddScoped<IFeatureWriter, CountingWriter>();
        services.AddSingleton<IMetadataV2GraphProvider>(new GraphProvider(Snapshot(JsonNode.Parse(Graph)!)));

        services.AddSourceBackedWriteGuard();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IFeatureWriter>().Should().BeOfType<SourceBackedWriteGuardFeatureWriter>();
        services.Count(static d => d.ServiceType == typeof(IFeatureWriter)).Should().Be(1);
    }

    private static async Task WriteAsync(IFeatureWriter writer, string kind, int layerId)
    {
        var batch = new FeatureEditBatch { Creates = [NewFeature(0)] };
        switch (kind)
        {
            case "create": await writer.CreateAsync(layerId, NewFeature(0)); break;
            case "update": await writer.UpdateAsync(layerId, NewFeature(1)); break;
            case "delete": await writer.DeleteAsync(layerId, 1); break;
            case "batch": await writer.ApplyEditsAsync(layerId, batch); break;
            case "receipt": await writer.ApplyEditsAsync(layerId, batch, Receipt()); break;
            default:
                await using (var transaction = await writer.BeginTransactionAsync())
                {
                    await transaction.ApplyEditsAsync(layerId, batch);
                }
                break;
        }
    }

    private static Feature NewFeature(long id)
        => new() { Id = id, Geometry = null, Attributes = ImmutableDictionary<string, object?>.Empty };

    private static FeatureEditIdempotencyReceipt Receipt()
        => new()
        {
            Scope = "scope",
            ExpiresAt = DateTimeOffset.UnixEpoch,
            Payload = static _ => ReadOnlyMemory<byte>.Empty
        };

    private static (SourceBackedWriteGuardFeatureWriter Writer, CountingWriter Inner) CreateWriter()
    {
        var inner = new CountingWriter();
        return (new SourceBackedWriteGuardFeatureWriter(inner, new GraphProvider(Snapshot(JsonNode.Parse(Graph)!))), inner);
    }

    private static MetadataV2GraphSnapshot Snapshot(JsonNode node)
        => new(JsonSerializer.Deserialize(node.ToJsonString(), MetadataV2JsonContext.Default.MetadataV2Graph)!,
            "etag", DateTimeOffset.UnixEpoch);

    private sealed class GraphProvider(MetadataV2GraphSnapshot? snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => snapshot is null
                ? throw new InvalidOperationException("No metadata graph is configured.")
                : ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);
    }

    private sealed class DisposedGraphProvider : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => throw new ObjectDisposedException(nameof(IMetadataV2GraphProvider));

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => throw new ObjectDisposedException(nameof(IMetadataV2GraphProvider));
    }

    private sealed class CountingWriter : IFeatureWriter
    {
        public int Calls { get; private set; }

        public Task<IFeatureWriterTransaction> BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IFeatureWriterTransaction>(new CountingTransaction(this));

        public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => Count(feature);

        public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => Count(feature);

        public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => Count(true);

        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
            => Count(default(FeatureEditResult));

        internal Task<T> Count<T>(T value)
        {
            Calls++;
            return Task.FromResult(value);
        }
    }

    private sealed class CountingTransaction(CountingWriter writer) : IFeatureWriterTransaction
    {
        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
            => writer.Count(default(FeatureEditResult));

        public Task<FeatureWriterTransactionCommitOutcome> CommitAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(default(FeatureWriterTransactionCommitOutcome));

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
