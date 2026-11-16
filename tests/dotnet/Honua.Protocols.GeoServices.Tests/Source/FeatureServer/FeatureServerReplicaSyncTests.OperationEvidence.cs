// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed partial class FeatureServerReplicaSyncTests
{
    private readonly ConcurrentUploadGate _concurrentUpload = new();

    public FeatureServerReplicaSyncTests()
    {
        _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
            .DecorateService<IFeatureWriter>(inner => new ConcurrentUploadWriter(inner, _concurrentUpload));
    }

    [IntegrationTest]
    [Operation(Operations.SynchronizeReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    public async Task SynchronizeReplica_ForeignCommitAfterUpload_RetainsExactConflictResolutionBase()
    {
        await EnsureOperationEvidenceCaptureAsync();
        var objectId = await AddFeatureAsync("evidence-base");
        var replicaId = await CreateReplicaAsync("ExactOperationEvidence", "0");
        await SynchronizeDownloadAsync(replicaId);
        await UpdateFeatureAsync(objectId, "server-conflict");
        _concurrentUpload.ObjectId = objectId;
        var edits = JsonSerializer.Serialize(new[]
        {
            new { id = 0, updates = new[] { new { attributes = new { objectid = objectId, name = "own-upload" } } } }
        });

        var upload = await SynchronizeUploadAsync(replicaId, edits);

        upload.GetProperty("appliedUpdates").GetInt32().Should().Be(1);
        var ownEvidence = _concurrentUpload.OwnEvidence.Should().ContainSingle().Subject;
        var foreignEvidence = _concurrentUpload.ForeignEvidence.Should().ContainSingle().Subject;
        foreignEvidence.Generation.Should().BeGreaterThan(ownEvidence.Generation);
        var conflictId = upload.GetProperty("conflicts")[0].GetProperty("conflictId").GetString()!;
        var record = await _fixture.GetService<IReplicaConflictRepository>().GetAsync(conflictId);
        record.Should().NotBeNull();
        record!.Value.ResolutionBaseGeneration.Should().Be(ownEvidence.Generation);
        upload.GetRawText().Should().NotContain("operationEvidence");
    }

    private Task EnsureOperationEvidenceCaptureAsync()
        => _fixture.Postgres.RunUnderSchemaMutationLockAsync(async () =>
        {
            // WebAppFixture seeds YAML and skips application migrations. Use the production
            // capture trigger on the shared change log rather than a test-only approximation.
            const string resource = "Honua.Server.Migrations.194_CaptureFeatureOperationEvidence.sql";
            await using var stream = typeof(PostgresFixture).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"The operation evidence migration {resource} was not embedded.");
            using var reader = new StreamReader(stream);
            var ddl = await reader.ReadToEndAsync();
            await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
            await using var command = connection.CreateCommand();
            command.CommandText = ddl;
            await command.ExecuteNonQueryAsync();
        });

    private sealed class ConcurrentUploadGate
    {
        public long ObjectId { get; set; }
        public ImmutableArray<FeatureOperationEvidence> OwnEvidence { get; set; } = [];
        public ImmutableArray<FeatureOperationEvidence> ForeignEvidence { get; set; } = [];
    }

    private sealed class ConcurrentUploadWriter(IFeatureWriter inner, ConcurrentUploadGate gate) : IFeatureWriter
    {
        public Task<IFeatureWriterTransaction> BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => inner.BeginTransactionAsync(isolationLevel, cancellationToken);

        public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => inner.CreateAsync(layerId, feature, cancellationToken);

        public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => inner.UpdateAsync(layerId, feature, cancellationToken);

        public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(layerId, featureId, cancellationToken);

        public async Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.ApplyEditsAsync(layerId, editBatch, cancellationToken);
            if (gate.ObjectId == 0 || editBatch.Updates.Length != 1 || editBatch.Updates[0].Id != gate.ObjectId)
            {
                return result;
            }

            gate.ObjectId = 0;
            gate.OwnEvidence = result.UpdateResults.Single().OperationEvidence;
            var foreign = editBatch.Updates[0] with
            {
                Attributes = editBatch.Updates[0].Attributes.SetItem("name", "foreign-after-upload")
            };
            var foreignResult = await inner.ApplyEditsAsync(layerId,
                FeatureEditBatch.Create(updates: [foreign]), cancellationToken);
            gate.ForeignEvidence = foreignResult.UpdateResults.Single().OperationEvidence;
            return result;
        }
    }
}
