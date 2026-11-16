// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Edit;

public sealed class FeatureEditGateTests
{
    private static readonly FeatureEditHookScope Scope = new("svc", 1) { LayerName = "layer", UserName = "alice", TraceId = "trace" };

    [UnitTest]
    public async Task CheckAsync_RollbackBatchWithContingentFailure_DoesNotCallTheHooks()
    {
        var hooks = new RejectingHooks("blocked");
        var batch = FeatureEditBatch.Create(creates: [Row("ok", "S"), Row("bad", "L")], rollbackOnFailure: true);

        var rejections = await FeatureEditGate.CheckAsync(hooks, new FeatureEditCheck(Resource(), Scope, batch), CancellationToken.None);

        rejections.Should().ContainSingle().Which.Index.Should().Be(1);
        hooks.Seen.Should().BeEmpty();
    }

    [UnitTest]
    public async Task CheckAsync_PartialBatch_RunsHooksOnTheRemainingRows_AndMapsTheirIndexesBack()
    {
        var hooks = new RejectingHooks("blocked");
        var batch = FeatureEditBatch.Create(creates: [Row("ok", "S"), Row("bad", "L"), Row("blocked", "S"), Row("last", "S")]);

        var rejections = await FeatureEditGate.CheckAsync(hooks, new FeatureEditCheck(Resource(), Scope, batch), CancellationToken.None);

        hooks.Seen.Should().ContainSingle().Which.Creates.Select(Name).Should().Equal("ok", "blocked", "last");
        rejections.Select(static rejection => (rejection.Index, rejection.Message)).Should().Equal(
            (1, "Attribute combination for fields (color, size) violates contingent-value group 'colorSize'."),
            (2, "blocked"));
    }

    [UnitTest]
    public async Task CheckAsync_PartialUpdate_ShowsTheHooksTheEffectiveRow()
    {
        var hooks = new RejectingHooks("stored");
        var partial = Feature.Create(7, null, ImmutableDictionary<string, object?>.Empty.Add("size", "S"));
        var batch = FeatureEditBatch.Create(updates: [partial]);
        var check = new FeatureEditCheck(Resource(), Scope, batch)
        {
            EffectiveAttributes = static row => Row("stored", "L").Attributes.SetItems(row.Feature!.Value.Attributes),
        };

        var rejections = await FeatureEditGate.CheckAsync(hooks, check, CancellationToken.None);

        rejections.Should().ContainSingle().Which.Message.Should().Be("blocked");
        var seen = hooks.Seen.Should().ContainSingle().Which.Updates.Should().ContainSingle().Subject;
        seen.Id.Should().Be(7);
        seen.Attributes.Should().Contain("color", "red").And.Contain("size", "S");
        batch.Updates[0].Attributes.Should().HaveCount(1);
    }

    [UnitTest]
    public async Task CheckAsync_HookRejectionOutsideTheRemainingRows_FailsInsteadOfBeingDropped()
    {
        var hooks = new FixedHooks(new FeatureEditHookRejection(FeatureEditOperationKind.Create, 5, "lost"));
        var batch = FeatureEditBatch.Create(creates: [Row("ok", "S"), Row("bad", "L")]);

        var act = () => FeatureEditGate.CheckAsync(hooks, new FeatureEditCheck(Resource(), Scope, batch), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public void RejectedResult_ReportsAClientError()
    {
        var batch = FeatureEditBatch.Create(creates: [Row("a", "S")]);

        var result = FeatureEditGate.RejectedResult(batch, [new(FeatureEditOperationKind.Create, 0, "no")]);

        result.WasRolledBack.Should().BeTrue();
        result.CreateResults.Should().ContainSingle().Which.ErrorCode.Should().Be(FeatureEditGate.RejectedErrorCode);
        FeatureEditGate.RejectedErrorCode.Should().BeInRange(400, 499);
    }

    [UnitTest]
    public void WithRejectedRows_PutsEachRejectionBackAtItsBatchPosition()
    {
        var batch = FeatureEditBatch.Create(creates: [Row("a", "S"), Row("b", "S"), Row("c", "S")], deletes: [7, 8]);
        IReadOnlyList<FeatureEditHookRejection> rejections =
        [
            new(FeatureEditOperationKind.Create, 1, "no b"),
            new(FeatureEditOperationKind.Delete, 0, "keep 7"),
        ];
        var written = FeatureEditResult.Success(
            2,
            0,
            1,
            createResults: [EditOperationResult.Success(10), EditOperationResult.Success(12)],
            deleteResults: [EditOperationResult.Success(8)]);

        var merged = FeatureEditGate.WithRejectedRows(written, batch, rejections);

        merged.CreateResults.Select(static result => (result.IsSuccess, result.ObjectId, result.ErrorMessage))
            .Should().Equal((true, (long?)10, (string?)null), (false, null, "no b"), (true, 12, null));
        merged.DeleteResults.Select(static result => (result.IsSuccess, result.ObjectId))
            .Should().Equal((false, (long?)7), (true, 8));
        FeatureEditGate.WithoutRejected(batch, rejections).Deletes.Should().Equal(8);
    }

    [UnitTest]
    public void WithoutRejected_OrderedOperations_DropsTheRowFromBothShapes()
    {
        var create = Row("a", "S");
        var batch = new FeatureEditBatch
        {
            Operations = [FeatureEditOperation.Delete(5), FeatureEditOperation.Create(create), FeatureEditOperation.Delete(6)],
            Creates = [create],
            Deletes = [5, 6],
        };

        var remaining = FeatureEditGate.WithoutRejected(batch, [new(FeatureEditOperationKind.Delete, 1, "no")]);

        remaining.Operations.Select(static operation => operation.ObjectId ?? operation.Feature?.Id)
            .Should().Equal(5, 0);
        remaining.Deletes.Should().Equal(5);
        remaining.Creates.Should().ContainSingle();
    }

    private static string? Name(Feature feature) => feature.Attributes["name"] as string;

    private static Feature Row(string name, string size)
        => Feature.Create(
            0,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("name", name).Add("color", "red").Add("size", size));

    private static MetadataV2Object Resource() => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "shirts", Name = "shirts" },
        ContingentValueGroups =
        [
            new MetadataV2ContingentValueGroup
            {
                Name = "colorSize",
                Restrictive = true,
                Fields = ["color", "size"],
                ContingentValues =
                [
                    new MetadataV2ContingentValue
                    {
                        Id = 1,
                        Values = new Dictionary<string, MetadataV2ContingentFieldValue>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["color"] = new() { Type = "code", Code = JsonElement.Parse("\"red\"") },
                            ["size"] = new() { Type = "code", Code = JsonElement.Parse("\"S\"") },
                        },
                    },
                ],
            },
        ],
    };

    private sealed class FixedHooks(FeatureEditHookRejection rejection) : IFeatureEditHooks
    {
        public bool HasHooks => true;

        public Task<IReadOnlyList<FeatureEditHookRejection>> RunBeforeAsync(
            FeatureEditHookScope scope,
            FeatureEditBatch batch,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FeatureEditHookRejection>>([rejection]);

        public Task RunAfterAsync(FeatureEditHookScope scope, FeatureEditOutcome outcome, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RejectingHooks(string blockedName) : IFeatureEditHooks
    {
        public List<FeatureEditBatch> Seen { get; } = [];

        public bool HasHooks => true;

        public Task<IReadOnlyList<FeatureEditHookRejection>> RunBeforeAsync(
            FeatureEditHookScope scope,
            FeatureEditBatch batch,
            CancellationToken cancellationToken)
        {
            Seen.Add(batch);
            IReadOnlyList<FeatureEditHookRejection> rejections =
            [
                .. FeatureEditRows.Enumerate(batch)
                    .Where(row => row.Feature is { } feature && Name(feature) == blockedName)
                    .Select(static row => new FeatureEditHookRejection(row.Kind, row.Index, "blocked")),
            ];
            return Task.FromResult(rejections);
        }

        public Task RunAfterAsync(FeatureEditHookScope scope, FeatureEditOutcome outcome, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
