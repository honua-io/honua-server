// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.FeatureStore;

public sealed partial class BranchVersioningIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEditsAsync_DurableOperationReceipt_ReplaysExactCommittedUpdates()
    {
        var store = CreateFeatureStore();
        var original = await store.CreateAsync(PointsLayerId, BuildFeature("before", 1, 1), CancellationToken.None);
        using var capture = FeatureOperationEvidenceScope.Begin();
        var batch = FeatureEditBatch.Create(operations:
        [
            FeatureEditOperation.Update(BuildFeature("first", 2, 2, original.Id)),
            FeatureEditOperation.Update(BuildFeature("second", 3, 3, original.Id))
        ], rollbackOnFailure: true);
        var receipt = new FeatureEditIdempotencyReceipt
        {
            Scope = "operation-replay",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Payload = _ => new byte[] { 1, 2, 3 }
        };

        var application = await store.ApplyEditsAsync(PointsLayerId, batch, receipt, CancellationToken.None);
        IFeatureEditIdempotencyReceipts receipts = new PostgresFeatureEditIdempotencyReceipts(_fixture.DataSource, _schema);
        var replay = await receipts.TryReadWithEvidenceAsync(receipt.Scope);

        application.ReceiptDurable.Should().BeTrue();
        application.Result.UpdateResults.Should().HaveCount(2);
        replay.Should().NotBeNull();
        replay!.Value.Payload.ToArray().Should().Equal(1, 2, 3);
        replay.Value.Evidence.Should().NotBeNull();
        replay.Value.Evidence!.Updates.Should().HaveCount(2);
        replay.Value.Evidence.Updates[0].Should().Equal(application.Result.UpdateResults[0].OperationEvidence);
        replay.Value.Evidence.Updates[1].Should().Equal(application.Result.UpdateResults[1].OperationEvidence);
        replay.Value.Evidence.Updates[1][0].Generation.Should()
            .BeGreaterThan(replay.Value.Evidence.Updates[0][0].Generation);
    }
}
