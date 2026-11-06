// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure.Events.Outbox;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using NSubstitute;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// Row-level security <c>WITH CHECK</c> on the shared Postgres write path (honua-server#6103):
/// a caller scoped by an RLS policy (<c>region IN (claim region)</c>) must not create a row
/// outside that scope or move a visible row out of it. Every protocol (GeoServices applyEdits,
/// OGC API Features, WFS-T, OData, gRPC) writes through
/// <c>PostgresFeatureStoreRefactored.ApplyEditsAsync</c> or its writer transaction.
/// </summary>
[Collection("Database")]
public sealed class PostgresRowSecurityWriteCheckIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const int LayerId = 61030;
    private const long NorthRowId = 1;

    // The exact predicate shape the RLS source translates "region IN (north)" to on the
    // managed features table (PostgresSqlFilterTranslator: "attributes" ->> 'region').
    private static readonly SqlFragment NorthOnly = new("(\"attributes\" ->> 'region') IN (@p0)", ["north"]);

    private readonly IRowLevelSecurityFilterSource _rowPolicies = Substitute.For<IRowLevelSecurityFilterSource>();
    private string _schema = null!;
    private PostgresFeatureStoreRefactored _store = null!;

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaInternalAsync(nameof(PostgresRowSecurityWriteCheckIntegrationTests), applySeed: false);
        await fixture.ExecuteAsync($$"""
            CREATE TABLE "{{_schema}}".features (
                objectid bigserial PRIMARY KEY,
                layer_id integer NOT NULL,
                geometry geometry,
                attributes jsonb NOT NULL DEFAULT '{}'::jsonb);
            INSERT INTO "{{_schema}}".features (objectid, layer_id, attributes)
                VALUES ({{NorthRowId}}, {{LayerId}}, '{"region":"north","name":"n1"}');
            SELECT setval(pg_get_serial_sequence('"{{_schema}}".features', 'objectid'), 100);
            """);

        _rowPolicies.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(NorthOnly);
        _store = CreateStore();
    }

    public Task DisposeAsync() => fixture.DropSchemaAsync(_schema);

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyEdits_AddOutsideRlsScope_FailsNotPermittedAndWritesNoRow(bool rollbackOnFailure)
    {
        var result = await _store.ApplyEditsAsync(LayerId, FeatureEditBatch.Create(
            creates: [Row(0, "south", "s1")],
            rollbackOnFailure: rollbackOnFailure));

        result.CreateResults.Should().ContainSingle();
        result.CreateResults[0].IsSuccess.Should().BeFalse();
        result.CreateResults[0].ErrorCode.Should().Be(EditOperationResult.NotPermittedErrorCode);
        result.CreateResults[0].IsCommitOutcomeUnknown.Should().BeFalse();
        (await CountRowsAsync("south")).Should().Be(0);
    }

    [IntegrationTest]
    public async Task ApplyEdits_MixedAddsWithoutRollback_KeepsInScopeRowAndRejectsOutOfScopeRow()
    {
        // More than one create on the autocommit path would otherwise take the bulk INSERT.
        var result = await _store.ApplyEditsAsync(LayerId, FeatureEditBatch.Create(
            creates: [Row(0, "north", "n2"), Row(0, "south", "s1"), Row(0, "north", "n3")]));

        result.CreateResults.Select(static r => r.IsSuccess).Should().Equal(true, false, true);
        result.CreateResults[1].ErrorCode.Should().Be(EditOperationResult.NotPermittedErrorCode);
        (await CountRowsAsync("north")).Should().Be(3);
        (await CountRowsAsync("south")).Should().Be(0);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyEdits_UpdateMovingRowOutOfRlsScope_FailsAndLeavesRowUnchanged(bool rollbackOnFailure)
    {
        var result = await _store.ApplyEditsAsync(LayerId, FeatureEditBatch.Create(
            updates: [Row(NorthRowId, "south", "moved")],
            rollbackOnFailure: rollbackOnFailure));

        result.UpdateResults.Should().ContainSingle();
        result.UpdateResults[0].IsSuccess.Should().BeFalse();
        result.UpdateResults[0].ErrorCode.Should().Be(EditOperationResult.NotPermittedErrorCode);
        (await ReadAttributesAsync(NorthRowId)).Should().Be("north|n1");
    }

    [IntegrationTest]
    public async Task ApplyEdits_UpdateWithinRlsScope_Succeeds()
    {
        var result = await _store.ApplyEditsAsync(LayerId, FeatureEditBatch.Create(
            updates: [Row(NorthRowId, "north", "renamed")]));

        result.UpdateResults.Should().ContainSingle().Which.IsSuccess.Should().BeTrue();
        (await ReadAttributesAsync(NorthRowId)).Should().Be("north|renamed");
    }

    [IntegrationTest]
    public async Task ApplyEdits_OrderedBatchWithRollback_OutOfScopeAddRollsBackWholeBatch()
    {
        var result = await _store.ApplyEditsAsync(LayerId, FeatureEditBatch.Create(
            operations:
            [
                FeatureEditOperation.Create(Row(0, "north", "n2")),
                FeatureEditOperation.Create(Row(0, "south", "s1")),
            ],
            rollbackOnFailure: true));

        result.WasRolledBack.Should().BeTrue();
        result.CreateResults[1].ErrorCode.Should().Be(EditOperationResult.NotPermittedErrorCode);
        (await CountRowsAsync("north")).Should().Be(1, "only the seeded row remains");
        (await CountRowsAsync("south")).Should().Be(0);
    }

    [IntegrationTest]
    public async Task WriterTransaction_OutOfScopeAdd_IsUndoneEvenWhenTheTransactionCommits()
    {
        // WFS-T multi-layer transactions use the cross-layer writer transaction. A failed slot
        // without rollbackOnFailure must not leave its row behind when the transaction commits.
        await using (var transaction = await _store.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            var result = await transaction.ApplyEditsAsync(LayerId, FeatureEditBatch.Create(
                operations:
                [
                    FeatureEditOperation.Create(Row(0, "south", "s1")),
                    FeatureEditOperation.Create(Row(0, "north", "n2")),
                ]));

            result.CreateResults.Select(static r => r.IsSuccess).Should().Equal(false, true);
            result.CreateResults[0].ErrorCode.Should().Be(EditOperationResult.NotPermittedErrorCode);
            (await transaction.CommitAsync()).Should().Be(FeatureWriterTransactionCommitOutcome.Committed);
        }

        (await CountRowsAsync("south")).Should().Be(0);
        (await CountRowsAsync("north")).Should().Be(2);
    }

    [IntegrationTest]
    public async Task ApplyEdits_NoRlsPolicy_WritesAnyRegion()
    {
        _rowPolicies.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns((SqlFragment?)null);

        var result = await _store.ApplyEditsAsync(LayerId, FeatureEditBatch.Create(
            creates: [Row(0, "south", "s1"), Row(0, "south", "s2")],
            updates: [Row(NorthRowId, "south", "moved")]));

        result.CreateResults.Should().OnlyContain(static r => r.IsSuccess);
        result.UpdateResults.Should().ContainSingle().Which.IsSuccess.Should().BeTrue();
        (await CountRowsAsync("south")).Should().Be(3);
    }

    [IntegrationTest]
    public async Task ApplyEdits_CallerSuppliedWriteCheck_IsReplacedByResolvedPolicy()
    {
        // Only the store stamps the predicate; a batch cannot widen its own write scope.
        var result = await _store.ApplyEditsAsync(LayerId, FeatureEditBatch.Create(
            creates: [Row(0, "south", "s1")]) with
        { RowSecurityWriteCheck = new SqlFragment("TRUE", []) });

        result.CreateResults[0].ErrorCode.Should().Be(EditOperationResult.NotPermittedErrorCode);
        (await CountRowsAsync("south")).Should().Be(0);
    }

    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ApplyEdits_ReplacementPreservesProtectedFieldsInsideWriterTransaction(bool ordered, bool rollbackOnFailure)
    {
        await fixture.ExecuteAsync($$"""
            UPDATE "{{_schema}}".features
            SET attributes = attributes || '{"code":"A","globalid":"stable-id","description":"remove","undeclared":"remove"}'::jsonb,
                geometry = ST_GeomFromText('POINT(1 2)', 4326)
            WHERE objectid = {{NorthRowId}};
            """);
        var replacement = Row(NorthRowId, "north", "after") with
        {
            PreservedAttributeNames = ["code", "globalid"],
            ExplicitAttributeRemovals = ["code"]
        };
        var batch = ordered
            ? FeatureEditBatch.Create(operations: [FeatureEditOperation.Update(replacement)], rollbackOnFailure: rollbackOnFailure)
            : FeatureEditBatch.Create(updates: [replacement], rollbackOnFailure: rollbackOnFailure);

        var result = await _store.ApplyEditsAsync(LayerId, batch);

        result.IsSuccess.Should().BeTrue();
        await using var command = fixture.DataSource.CreateCommand(
            $"SELECT attributes::text, geometry IS NULL FROM \"{_schema}\".features WHERE objectid = $1");
        command.Parameters.AddWithValue(NorthRowId);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        using var attributes = JsonDocument.Parse(reader.GetString(0));
        attributes.RootElement.GetProperty("name").GetString().Should().Be("after");
        attributes.RootElement.GetProperty("code").GetString().Should().Be("A");
        attributes.RootElement.GetProperty("globalid").GetString().Should().Be("stable-id");
        attributes.RootElement.TryGetProperty("description", out _).Should().BeFalse();
        attributes.RootElement.TryGetProperty("undeclared", out _).Should().BeFalse();
        reader.GetBoolean(1).Should().BeTrue();
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyEdits_HiddenRlsRow_DeleteIsRejected(bool ordered)
    {
        await InsertHiddenRowAsync();
        var batch = ordered
            ? FeatureEditBatch.Create(operations: [FeatureEditOperation.Delete(2)])
            : FeatureEditBatch.Create(deletes: [2]);

        var result = await _store.ApplyEditsAsync(LayerId, batch);

        result.DeleteResults.Should().ContainSingle().Which.IsSuccess.Should().BeFalse();
        (await CountRowsAsync("south")).Should().Be(1);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyEdits_HiddenRlsRow_UpdateIntoVisiblePartitionIsRejected(bool ordered)
    {
        await InsertHiddenRowAsync();
        var update = Row(2, "north", "stolen") with { PreserveOmittedMaskedAttributes = true };
        var batch = ordered
            ? FeatureEditBatch.Create(operations: [FeatureEditOperation.Update(update)])
            : FeatureEditBatch.Create(updates: [update]);

        var result = await _store.ApplyEditsAsync(LayerId, batch);

        result.UpdateResults.Should().ContainSingle().Which.IsSuccess.Should().BeFalse();
        (await ReadAttributesAsync(2)).Should().Be("south|private");
    }

    [IntegrationTheory]
    [InlineData("create", false)]
    [InlineData("update", false)]
    [InlineData("delete", false)]
    [InlineData("create", true)]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    public async Task ApplyEdits_HiddenRlsRow_SingleWriterCannotBypassPolicy(string operation, bool withOutbox)
    {
        await InsertHiddenRowAsync();
        var outbox = new RecordingOutboxWriter();
        _store = CreateStore(outbox);
        using var scope = withOutbox ? BeginOutboxScope() : null;
        Func<Task> write = operation switch
        {
            "create" => async () => { await _store.CreateAsync(LayerId, Row(0, "south", "new-private")); },
            "update" => async () => { await _store.UpdateAsync(LayerId, Row(2, "north", "stolen")); },
            _ => async () => { await _store.DeleteAsync(LayerId, 2); }
        };

        await write.Should().ThrowAsync<ResourceAccessDeniedException>();
        outbox.Writes.Should().Be(0);
        (await ReadAttributesAsync(2)).Should().Be("south|private");
        (await CountRowsAsync("south")).Should().Be(1);
    }

    [IntegrationTheory]
    [InlineData("create", false)]
    [InlineData("update", false)]
    [InlineData("delete", false)]
    [InlineData("create", true)]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    public async Task ApplyEdits_HiddenRlsRow_SingleWriterInScopePreservesCommitAndReturningContract(string operation, bool withOutbox)
    {
        var outbox = new RecordingOutboxWriter();
        _store = CreateStore(outbox);
        using var scope = withOutbox ? BeginOutboxScope() : null;
        if (operation == "delete")
        {
            (await _store.DeleteAsync(LayerId, NorthRowId)).Should().BeTrue();
            (await CountRowsAsync("north")).Should().Be(0);
        }
        else
        {
            var returned = operation == "create"
                ? await _store.CreateAsync(LayerId, Row(0, "north", "accepted"))
                : await _store.UpdateAsync(LayerId, Row(NorthRowId, "north", "accepted"));
            returned.Id.Should().BePositive();
            returned.Attributes.Should().ContainKey("objectid");
            (await ReadAttributesAsync(returned.Id)).Should().Be("north|accepted");
        }
        outbox.Writes.Should().Be(withOutbox ? 1 : 0);
    }

    private static IDisposable BeginOutboxScope()
        => FeatureMutationOutboxScope.Begin(new FeatureMutationOutboxScopeData
        {
            EntryFactory = (id, kind, _) => new FeatureChangeOutboxEntry
            {
                OutboxId = Guid.NewGuid(), ServiceId = "regions", LayerId = LayerId, ObjectId = id,
                Operation = kind, Protocol = "test", RequestId = "policy-test", EventId = "policy-test",
                EventPayload = "{}", Status = "pending", RetryCount = 0, CreatedAt = DateTimeOffset.UtcNow
            }
        });

    private Task InsertHiddenRowAsync()
        => fixture.ExecuteAsync($$"""
            INSERT INTO "{{_schema}}".features (objectid, layer_id, attributes)
            VALUES (2, {{LayerId}}, '{"region":"south","name":"private","secret":"hidden"}');
            """);

    private static Feature Row(long id, string region, string name)
        => Feature.Create(
            id,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("region", region).Add("name", name));

    private async Task<long> CountRowsAsync(string region)
    {
        await using var command = fixture.DataSource.CreateCommand(
            $"SELECT count(*) FROM \"{_schema}\".features WHERE layer_id = $1 AND attributes->>'region' = $2");
        command.Parameters.AddWithValue(LayerId);
        command.Parameters.AddWithValue(region);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> ReadAttributesAsync(long objectId)
    {
        await using var command = fixture.DataSource.CreateCommand(
            $"SELECT (attributes->>'region') || '|' || (attributes->>'name') FROM \"{_schema}\".features WHERE objectid = $1");
        command.Parameters.AddWithValue(objectId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private PostgresFeatureStoreRefactored CreateStore(IFeatureChangeOutboxWriter? outbox = null)
    {
        var connections = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        connections.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
            (DbConnection)await fixture.DataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
        connections.OpenTransactionAsync(Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            DbConnection connection = await fixture.DataSource.OpenConnectionAsync(call.Arg<CancellationToken>());
            DbTransaction transaction = await connection.BeginTransactionAsync(call.Arg<IsolationLevel>(), call.Arg<CancellationToken>());
            return (connection, transaction);
        });

        var cacheManager = Substitute.For<IFeatureCacheManager>();
        cacheManager.GetGeometryStorageTypeAsync(Arg.Any<CancellationToken>()).Returns(GeometryStorageType.Geometry);
        cacheManager.GetLayerSridAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((int?)null);

        var dictionaryPool = new DefaultObjectPoolProvider().Create(new DefaultPooledObjectPolicy<Dictionary<string, object?>>());
        var dataAccess = new FeatureDataAccess(new FeatureDataAccessDependencies(
            connections, new GeometryProcessor(), cacheManager, dictionaryPool,
            null, NullLogger<FeatureDataAccess>.Instance, null, null, null, _schema, OutboxRepository: outbox));

        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "rls-write-check", Name = "regions" },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            SchemaFields =
            [
                new() { Name = "region", Type = MetadataV2FieldType.String },
                new() { Name = "name", Type = MetadataV2FieldType.String },
            ],
        };
        var binding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "rls-write-check-binding", Name = "rls-write-check-binding" },
            ResourceId = resource.Metadata.Id,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = $"{_schema}.features",
            StorageLayerId = LayerId,
        };
        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Revision = 1, Resources = [resource], StorageBindings = [binding] },
            "\"rls-write-check\"",
            DateTimeOffset.UnixEpoch);

        return new PostgresFeatureStoreRefactored(
            new FeatureQueryBuilder(
                new DefaultObjectPoolProvider().Create(new FeatureStoreStringBuilderPooledObjectPolicy()),
                new GeometryProcessor(),
                _schema),
            dataAccess,
            cacheManager,
            connections,
            dictionaryPool,
            connectionEncryptionService: null,
            v2Provider: new FixedGraphProvider(snapshot),
            rlsFilterSource: _rowPolicies);
    }

    private sealed class FixedGraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(revision == snapshot.Revision ? snapshot : null);
    }
    private sealed class RecordingOutboxWriter : IFeatureChangeOutboxWriter
    {
        public int Writes { get; private set; }

        public Task WriteOutboxRowAsync(DbConnection connection, DbTransaction transaction,
            FeatureChangeOutboxEntry entry, CancellationToken cancellationToken)
        {
            Writes++;
            return Task.CompletedTask;
        }
    }

}
