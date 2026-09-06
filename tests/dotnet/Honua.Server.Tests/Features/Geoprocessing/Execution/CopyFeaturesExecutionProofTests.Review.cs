// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.Infrastructure.Helpers;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed partial class CopyFeaturesExecutionProofTests
{
    [Fact]
    public async Task CopyFeatures_MaskedRequiredField_CopiesRedactedValuesAndNullableSchema()
    {
        _masks.ResolveAsync(Arg.Is<MetadataV2Resource>(r => r.Metadata.Id == _sourceResource.Metadata.Id), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("label"));
        var result = await _fixture.GetService<IFeatureLayerCopyService>().CopyAsync(_sourceId, "Masked copy",
            new FeatureQuery(), Guid.NewGuid().ToString("N"), 1_000_000, CancellationToken.None);
        result.FeatureCount.Should().Be(3);
        var snapshot = await _fixture.GetService<IMetadataV2GraphProvider>().GetCurrentAsync();
        var target = snapshot.Index.ResourcesByStorageLayerId[result.LayerId];
        target.SchemaFields.Select(f => f.Name).Should().BeEquivalentTo(
            _sourceResource.SchemaFields.Select(f => f.Name));
        target.SchemaFields.Single(f => f.Name == "label").Nullable.Should().BeTrue();
        target.SchemaFields.Single(f => f.Name == "score").Nullable.Should().BeFalse();
        var publication = snapshot.Graph.Publications.Single(p => p.ResourceId == target.Metadata.Id);
        var reader = await _fixture.GetService<FeatureProviderQueryRouter>().ResolveReaderAsync(snapshot,
            snapshot.Index.ServicesById[publication.ServiceId], target, publication, result.LayerId, FeatureProviderReadOperation.Query);
        var rows = (await reader.QueryAsync(result.LayerId, new FeatureQuery { IncludeZ = true })).Items;
        rows.Select(r => r.Id).Should().BeEquivalentTo(11L, 13L, 15L);
        foreach (var row in rows)
        {
            row.Attributes["label"].Should().BeNull("masked source values must never be copied");
            Convert.ToInt32(row.Attributes["score"], System.Globalization.CultureInfo.InvariantCulture)
                .Should().Be(row.Id switch { 11 => 7, 13 => 14, 15 => 21, _ => -1 });
        }
        _masks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>()).Returns(ImmutableArray<string>.Empty);
        await AssertRows(_sourceId, [11, 13, 15]);
    }

    [Theory]
    [InlineData("before-publication")]
    [InlineData("after-publication")]
    [InlineData("metadata-rewrite")]
    [InlineData("cancel-enablement")]
    public async Task CopyFeatures_PublicationFailure_RemovesOwnedTableAndCatalog(string stage)
    {
        var realPublisher = _fixture.GetService<ILayerPublishingService>();
        var publisher = Substitute.For<ILayerPublishingService>();
        publisher.PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (stage == "before-publication")
                {
                    throw new InvalidOperationException("Injected pre-publication failure");
                }
                var published = await realPublisher.PublishLayerAsync(call.ArgAt<string>(0), call.ArgAt<LayerPublishRequest>(1), call.ArgAt<CancellationToken>(2));
                if (stage == "after-publication")
                {
                    throw new InvalidOperationException("Injected lost publication response");
                }
                return published;
            });
        using var cancelled = new CancellationTokenSource();
        publisher.SetLayerEnabledAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), true, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancelled.Cancel();
                throw new OperationCanceledException(cancelled.Token);
            });
        var realMetadata = _fixture.GetService<IMetadataV2GraphStore>();
        var metadata = Substitute.For<IMetadataV2GraphStore>();
        metadata.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(call => realMetadata.GetCurrentAsync(call.Arg<CancellationToken>()));
        metadata.SaveAsync(Arg.Any<MetadataV2Graph>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var graph = call.Arg<MetadataV2Graph>();
                if (stage == "metadata-rewrite" && graph.Resources.Any(r => r.Metadata.Name == "Failed copy" && r.Metadata.Annotations.ContainsKey("gp.operationId")))
                {
                    throw new InvalidOperationException("Injected metadata rewrite failure");
                }
                return realMetadata.SaveAsync(graph, call.Arg<string?>(), call.Arg<CancellationToken>());
            });
        var service = new PostgresFeatureLayerCopyService(_fixture.GetService<FeatureProviderQueryRouter>(),
            _fixture.GetService<IAdoNetDatabaseConnectionProvider>(), metadata, publisher, _masks);
        var before = await _fixture.GetService<IMetadataV2GraphProvider>().GetCurrentAsync();
        var action = () => service.CopyAsync(_sourceId, "Failed copy", new FeatureQuery(), Guid.NewGuid().ToString("N"), 1_000_000, cancelled.Token);
        if (stage == "cancel-enablement")
        {
            await action.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await action.Should().ThrowAsync<InvalidOperationException>();
        }
        var after = await _fixture.GetService<IMetadataV2GraphProvider>().GetCurrentAsync();
        after.Graph.Resources.Select(r => r.Metadata.Id).Should().BeEquivalentTo(before.Graph.Resources.Select(r => r.Metadata.Id));
        after.Graph.StorageBindings.Select(b => b.Metadata.Id).Should().BeEquivalentTo(before.Graph.StorageBindings.Select(b => b.Metadata.Id));
        after.Graph.Publications.Select(p => p.Metadata.Id).Should().BeEquivalentTo(before.Graph.Publications.Select(p => p.Metadata.Id));
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM information_schema.tables WHERE table_schema = @schema AND table_name LIKE 'gp_copy_%')
                 + (SELECT count(*) FROM honua.layers WHERE table_schema = @schema AND table_name LIKE 'gp_copy_%')
            """, connection);
        command.Parameters.AddWithValue("schema", _fixture.CurrentSchema!);
        Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(0);
        await AssertRows(_sourceId, [11, 13, 15]);
    }

    [Theory]
    [InlineData("score >= 14", null, false, 2)]
    [InlineData("score > 100", null, false, 0)]
    [InlineData(null, new long[] { 13 }, false, 1)]
    [InlineData(null, null, true, 3)]
    public async Task CopyFeatures_FilteredTemporalSource_DoesNotAdvertiseSourceExtent(string? where, long[]? objectIds, bool maskTime, int expectedCount)
    {
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            ALTER TABLE "{_fixture.CurrentSchema}".copyproof ADD COLUMN observed timestamptz NOT NULL DEFAULT '2026-01-01T00:00:00Z';
            UPDATE "{_fixture.CurrentSchema}".copyproof SET observed = '2026-01-01T00:00:00Z'::timestamptz + (id - 11) * interval '1 day';
            """, connection);
        await command.ExecuteNonQueryAsync();
        var store = _fixture.GetService<IMetadataV2GraphStore>();
        var before = await store.GetCurrentAsync();
        var temporal = _sourceResource with
        {
            SchemaFields = [.. _sourceResource.SchemaFields, new MetadataV2Field { Name = "observed", Type = MetadataV2FieldType.DateTime, Nullable = false }],
            Temporal = new MetadataV2ResourceTemporal
            {
                StartTimeField = "observed",
                Extent = new MetadataV2TimeRange
                {
                    Start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    End = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero)
                }
            }
        };
        await store.SaveAsync(before.Graph with
        {
            Resources = before.Graph.Resources.Select(r => r.Metadata.Id == temporal.Metadata.Id ? temporal : r).ToArray()
        }, before.Etag);
        if (maskTime)
        {
            _masks.ResolveAsync(Arg.Is<MetadataV2Resource>(r => r.Metadata.Id == _sourceResource.Metadata.Id), Arg.Any<CancellationToken>())
                .Returns(ImmutableArray.Create("observed"));
        }
        var result = await _fixture.GetService<IFeatureLayerCopyService>().CopyAsync(_sourceId, "Temporal copy",
            new FeatureQuery { Where = where, ObjectIds = objectIds?.ToImmutableArray() }, Guid.NewGuid().ToString("N"), 1_000_000, CancellationToken.None);
        result.FeatureCount.Should().Be(expectedCount);
        var after = await store.GetCurrentAsync();
        var target = after.Index.ResourcesByStorageLayerId[result.LayerId];
        target.Temporal!.StartTimeField.Should().Be("observed");
        target.Temporal.Extent.Should().BeNull("the canonical metadata resolver must derive the selected live extent");
        var publication = after.Graph.Publications.Single(p => p.ResourceId == target.Metadata.Id);
        var reader = await _fixture.GetService<FeatureProviderQueryRouter>().ResolveReaderAsync(after,
            after.Index.ServicesById[publication.ServiceId], target, publication, result.LayerId, FeatureProviderReadOperation.Query);
        var range = await TemporalExtentHelpers.TryResolveTemporalRangeV2Async(target, result.LayerId, reader, CancellationToken.None);
        range.Should().NotBeNull();
        range!.Value.Min.Should().Be(expectedCount == 0 || maskTime ? null : new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero));
        range.Value.Max.Should().Be(expectedCount == 0 || maskTime ? null : new DateTimeOffset(2026, 1, objectIds is null ? 5 : 3, 0, 0, 0, TimeSpan.Zero));
        after.Index.ResourcesByStorageLayerId[_sourceId].Temporal.Should().BeEquivalentTo(temporal.Temporal);
    }
}
