// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

// gRPC ApplyEdits runs the contingent-value check and the plugin edit hooks like GeoServices applyEdits.
[Protocol(TestProtocols.Grpc)]
[Operation(Operations.ApplyEdits)]
public sealed class GrpcApplyEditsGateTests : IDisposable
{
    private const long StoredId = 11;
    private readonly RecordingHooks _hooks = new();
    private readonly List<FeatureEditBatch> _written = [];
    private readonly GrpcApplyEditsReceipts _receipts = new();

    public void Dispose() => _receipts.Dispose();

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ContingentViolation_OnAddOrMergedUpdate_FailsTheCallBeforeWriting()
    {
        var harness = Harness();

        var add = await CallAsync(harness, Request(rollback: true, adds: [Shirt("red", "L")]));
        var update = await CallAsync(harness, Request(rollback: true, updates: [Update(size: "L")]));

        foreach (var exception in new[] { add, update })
        {
            exception.Should().NotBeNull();
            exception!.StatusCode.Should().Be(StatusCode.InvalidArgument);
            exception.Status.Detail.Should().Contain("contingent-value group 'colorSize'");
        }

        _written.Should().BeEmpty();
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task PartialUpdate_IsCheckedAgainstTheMergedStoredRow()
    {
        var harness = Harness();

        var response = await harness.Service.ApplyEdits(
            Request(rollback: true, updates: [Update(size: "S")]),
            Context(harness));

        response.UpdateResults.Should().ContainSingle().Which.Success.Should().BeTrue(
            "the stored colour completes a valid combination with the updated size");
        _written.Should().ContainSingle();
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task PluginRejection_WithRollback_FailsTheCallBeforeWriting()
    {
        var exception = await CallAsync(
            Harness(),
            Request(rollback: true, adds: [Shirt("red", "S"), Shirt("red", "S", RecordingHooks.BlockedName)]));

        exception!.StatusCode.Should().Be(StatusCode.InvalidArgument);
        exception.Status.Detail.Should().Be("rejected by plugin");
        _written.Should().BeEmpty();
        _hooks.AfterBatches.Should().BeEmpty();
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task Rejections_WithoutRollback_FailOnlyTheirRows()
    {
        var harness = Harness();
        var request = Request(
            rollback: false,
            adds: [Shirt("red", "S", "first"), Shirt("red", "S", RecordingHooks.BlockedName), Shirt("red", "L", "invalid"), Shirt("red", "S", "last")]);

        var response = await harness.Service.ApplyEdits(request, Context(harness));

        response.AddResults.Select(static result => result.Success).Should().Equal(true, false, false, true);
        response.AddResults[1].Error.Message.Should().Be("rejected by plugin");
        response.AddResults[2].Error.Message.Should().Contain("colorSize");
        _written.Should().ContainSingle().Which.Creates.Select(Name).Should().Equal("first", "last");
        _hooks.BeforeBatches.Should().ContainSingle().Which.Creates.Should().HaveCount(3, "the contingent failure never reaches the hooks");
        _hooks.AfterBatches.Should().ContainSingle().Which.Creates.Select(Name).Should().Equal("first", "last");
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task AfterHooks_SeeOnlyTheCommittedRows()
    {
        var harness = Harness(failSecondCreate: true);

        await harness.Service.ApplyEdits(
            Request(rollback: false, adds: [Shirt("red", "S", "kept"), Shirt("red", "S", "failed")]),
            Context(harness));

        var (batch, result) = _hooks.AfterCalls.Should().ContainSingle().Subject;
        FeatureEditRows.Enumerate(batch)
            .Where(row => FeatureEditRows.IsCommitted(result, row))
            .Select(static row => Name(row.Feature!.Value))
            .Should().Equal("kept");
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task KeyedReplay_DoesNotRunTheHooksAgain()
    {
        var harness = Harness();
        var request = Request(rollback: true, adds: [Shirt("red", "S")]);
        request.IdempotencyKey = "gate-retry";

        var first = await harness.Service.ApplyEdits(request, Context(harness));
        var replay = await harness.Service.ApplyEdits(request, Context(harness));

        replay.AddResults[0].ObjectId.Should().Be(first.AddResults[0].ObjectId);
        _written.Should().ContainSingle();
        _hooks.BeforeBatches.Should().ContainSingle();
        _hooks.AfterBatches.Should().ContainSingle();
    }

    private GrpcApplyEditsIdempotencyTests.ServiceHarness Harness(bool failSecondCreate = false)
        => new(
            _receipts,
            StoredRowReader(),
            RecordingWriter(failSecondCreate),
            ShirtResource(),
            services => services.AddSingleton<IFeatureEditHooks>(_hooks));

    private static ServerCallContext Context(GrpcApplyEditsIdempotencyTests.ServiceHarness harness)
        => harness.Context(GrpcApplyEditsIdempotencyTests.Subject("editor", issuer: "https://issuer-a.example"), tenantId: null);

    private static async Task<RpcException?> CallAsync(GrpcApplyEditsIdempotencyTests.ServiceHarness harness, Proto.ApplyEditsRequest request)
    {
        try
        {
            await harness.Service.ApplyEdits(request, Context(harness));
            return null;
        }
        catch (RpcException exception)
        {
            return exception;
        }
    }

    private static Proto.ApplyEditsRequest Request(
        bool rollback,
        Proto.Feature[]? adds = null,
        Proto.Feature[]? updates = null)
    {
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0, RollbackOnFailure = rollback };
        request.Adds.AddRange(adds ?? []);
        request.Updates.AddRange(updates ?? []);
        return request;
    }

    private static Proto.Feature Shirt(string color, string size, string name = "shirt")
        => new()
        {
            Attributes =
            {
                ["name"] = new Proto.AttributeValue { StringValue = name },
                ["color"] = new Proto.AttributeValue { StringValue = color },
                ["size"] = new Proto.AttributeValue { StringValue = size },
            },
        };

    private static Proto.Feature Update(string size)
        => new() { Id = StoredId, Attributes = { ["size"] = new Proto.AttributeValue { StringValue = size } } };

    private static string? Name(Feature feature) => feature.Attributes.GetValueOrDefault("name") as string;

    private static IFeatureReader StoredRowReader()
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.GetAsync(Arg.Any<int>(), StoredId, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Feature?>(Feature.Create(
            StoredId,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "stored").Add("color", "red").Add("size", "S"))));
        return reader;
    }

    private IFeatureWriter RecordingWriter(bool failSecondCreate)
    {
        FeatureEditResult Write(FeatureEditBatch batch)
        {
            _written.Add(batch);
            var creates = batch.Creates.Select((_, index) => failSecondCreate && index == 1
                ? EditOperationResult.Failure("write failed")
                : EditOperationResult.Success(500 + index));
            var updates = batch.Updates.Select(static update => EditOperationResult.Success(update.Id));
            return FeatureEditResult.Success(batch.Creates.Length, batch.Updates.Length, 0, createResults: [.. creates], updateResults: [.. updates]);
        }

        var writer = Substitute.For<IFeatureWriter>();
        writer.ApplyEditsAsync(default, default, default)
            .ReturnsForAnyArgs(call => Task.FromResult(Write(call.ArgAt<FeatureEditBatch>(1))));
        writer.ApplyEditsAsync(default, default, default(FeatureEditIdempotencyReceipt), default)
            .ReturnsForAnyArgs(call => Task.FromResult(new FeatureEditApplication
            {
                Result = Write(call.ArgAt<FeatureEditBatch>(1)),
                ReceiptDurable = false,
            }));
        return writer;
    }

    private static MetadataV2Object ShirtResource()
    {
        var resource = GrpcApplyEditsIdempotencyTests.ServiceHarness.CreateResource();
        return resource with
        {
            SchemaFields =
            [
                .. resource.SchemaFields,
                new MetadataV2Field { Name = "color", Type = MetadataV2FieldType.String, Length = 20, Nullable = true },
                new MetadataV2Field { Name = "size", Type = MetadataV2FieldType.String, Length = 20, Nullable = true },
            ],
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
    }

    private sealed class RecordingHooks : IFeatureEditHooks
    {
        public const string BlockedName = "blocked";

        public List<FeatureEditBatch> BeforeBatches { get; } = [];

        public List<(FeatureEditBatch Batch, FeatureEditResult Result)> AfterCalls { get; } = [];

        public IEnumerable<FeatureEditBatch> AfterBatches => AfterCalls.Select(static call => call.Batch);

        public bool HasHooks => true;

        public Task<IReadOnlyList<FeatureEditHookRejection>> RunBeforeAsync(
            FeatureEditHookScope scope,
            FeatureEditBatch batch,
            CancellationToken cancellationToken)
        {
            BeforeBatches.Add(batch);
            IReadOnlyList<FeatureEditHookRejection> rejections =
            [
                .. FeatureEditRows.Enumerate(batch)
                    .Where(static row => row.Feature is { } feature && Name(feature) == BlockedName)
                    .Select(static row => new FeatureEditHookRejection(row.Kind, row.Index, "rejected by plugin")),
            ];
            return Task.FromResult(rejections);
        }

        public Task RunAfterAsync(
            FeatureEditHookScope scope,
            FeatureEditBatch batch,
            FeatureEditResult result,
            CancellationToken cancellationToken)
        {
            AfterCalls.Add((batch, result));
            return Task.CompletedTask;
        }
    }
}
