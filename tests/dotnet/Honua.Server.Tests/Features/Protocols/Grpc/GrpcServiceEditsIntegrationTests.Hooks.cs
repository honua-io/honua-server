// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcServiceEditsIntegrationTests
{
    private readonly ServiceRecordingHooks _hooks = new();

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyServiceEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyServiceEdits")]
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyServiceEdits_HookRejection_RespectsRollbackAndReplaysWithoutHooks(bool rollback)
    {
        _hooks.RejectLayer = 1;
        var beforeFirst = await _fixture.CountStoredFeaturesAsync(0);
        var beforeSecond = await _fixture.CountStoredFeaturesAsync(1);
        var request = Request();
        request.RollbackOnFailure = rollback;
        request.IdempotencyKey = Guid.NewGuid().ToString("N");

        var response = await _client!.ApplyServiceEditsAsync(request, _headers);

        (response.Error is not null).Should().Be(rollback);
        response.Results[0].Results.AddResults.Single().Success.Should().Be(!rollback);
        response.Results[1].Results.AddResults.Single().Success.Should().BeFalse();
        (await _fixture.CountStoredFeaturesAsync(0)).Should().Be(beforeFirst + (rollback ? 0 : 1));
        (await _fixture.CountStoredFeaturesAsync(1)).Should().Be(beforeSecond);
        _hooks.BeforeLayers.Should().Equal(0, 1);
        _hooks.AfterCalls.Should().HaveCount(rollback ? 0 : 2);
        if (!rollback)
        {
            _hooks.AfterCalls.Single(call => call.Layer == 0).Outcome.Batch.Creates.Should().ContainSingle();
            _hooks.AfterCalls.Single(call => call.Layer == 1).Outcome.Batch.Creates.Should().BeEmpty();
        }

        var replay = await _client.ApplyServiceEditsAsync(request, _headers);
        replay.Should().BeEquivalentTo(response);
        _hooks.BeforeLayers.Should().Equal(0, 1);
        _hooks.AfterCalls.Should().HaveCount(rollback ? 0 : 2);
        (await _fixture.CountStoredFeaturesAsync(0)).Should().Be(beforeFirst + (rollback ? 0 : 1));
    }

    private sealed class ServiceRecordingHooks : IFeatureEditHooks
    {
        public int? RejectLayer { get; set; }
        public List<int> BeforeLayers { get; } = [];
        public List<(int Layer, FeatureEditOutcome Outcome)> AfterCalls { get; } = [];
        public bool HasHooks => true;

        public Task<IReadOnlyList<FeatureEditHookRejection>> RunBeforeAsync(
            FeatureEditHookScope scope, FeatureEditBatch batch, CancellationToken cancellationToken)
        {
            BeforeLayers.Add(scope.LayerId);
            IReadOnlyList<FeatureEditHookRejection> rejected = scope.LayerId == RejectLayer
                ? [.. FeatureEditRows.Enumerate(batch).Select(row =>
                    new FeatureEditHookRejection(row.Kind, row.Index, "rejected by plugin"))]
                : [];
            return Task.FromResult(rejected);
        }

        public Task RunAfterAsync(FeatureEditHookScope scope, FeatureEditOutcome outcome, CancellationToken cancellationToken)
        {
            AfterCalls.Add((scope.LayerId, outcome));
            return Task.CompletedTask;
        }
    }
}
