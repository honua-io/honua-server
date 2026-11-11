// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Services;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

// Keyed gRPC ApplyEdits retries: a stored result is replayed only to the same
// issuer-qualified actor in the same effective tenant, and a keyed edit commits at most
// once across nodes because the receipt committed with the edit decides the winner.
[Protocol(TestProtocols.Grpc)]
[Operation(Operations.ApplyEdits)]
public sealed class GrpcApplyEditsIdempotencyTests
{
    private const string ClientKey = "client-retry-key";

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_SameActorTenantAndKey_ReplaysTheFirstResult()
    {
        var harness = new ServiceHarness(new GrpcApplyEditsReceipts());
        var user = Subject("editor", issuer: "https://issuer-a.example");

        var first = await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: "tenant-a"));
        var retry = await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: "tenant-a"));

        harness.WriteCount.Should().Be(1);
        retry.AddResults[0].ObjectId.Should().Be(first.AddResults[0].ObjectId);
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_SameKeyInAnotherTenant_ExecutesItsOwnEdit()
    {
        var harness = new ServiceHarness(new GrpcApplyEditsReceipts());
        var user = Subject("editor", issuer: "https://issuer-a.example");

        var tenantA = await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: "tenant-a"));
        var tenantB = await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: "tenant-b"));

        harness.WriteCount.Should().Be(2, "each tenant's keyed edit must reach its own writer");
        tenantB.AddResults[0].ObjectId.Should().NotBe(tenantA.AddResults[0].ObjectId);
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_SameSubjectFromAnotherIssuer_ExecutesItsOwnEdit()
    {
        var harness = new ServiceHarness(new GrpcApplyEditsReceipts());

        var first = await harness.Service.ApplyEdits(
            AddRequest(), harness.Context(Subject("shared-sub", issuer: "https://issuer-a.example"), tenantId: null));
        var second = await harness.Service.ApplyEdits(
            AddRequest(), harness.Context(Subject("shared-sub", issuer: "https://issuer-b.example"), tenantId: null));

        harness.WriteCount.Should().Be(2, "a subject from another issuer is a different actor");
        second.AddResults[0].ObjectId.Should().NotBe(first.AddResults[0].ObjectId);
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_SameKeyFromAnotherAuthenticationScheme_ExecutesItsOwnEdit()
    {
        var harness = new ServiceHarness(new GrpcApplyEditsReceipts());

        await harness.Service.ApplyEdits(
            AddRequest(), harness.Context(Subject("shared-sub", issuer: null, scheme: "SchemeA"), tenantId: null));
        await harness.Service.ApplyEdits(
            AddRequest(), harness.Context(Subject("shared-sub", issuer: null, scheme: "SchemeB"), tenantId: null));

        harness.WriteCount.Should().Be(2);
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task KeyedEdits_WithDistinctKeys_DoNotRetainKeyGates()
    {
        using var store = new GrpcApplyEditsReceipts();
        var harness = new ServiceHarness(store);
        var user = Subject("editor", issuer: "https://issuer-a.example");

        for (var i = 0; i < 50; i++)
        {
            await harness.Service.ApplyEdits(AddRequest($"key-{i}"), harness.Context(user, tenantId: null));
        }

        harness.WriteCount.Should().Be(50);
        store.GateCount.Should().Be(0, "a key gate with no holder or waiter must be released");
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_KeyedEditWithoutBindableIdentity_IsRefusedBeforeWriting()
    {
        var harness = new ServiceHarness(new GrpcApplyEditsReceipts());
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "Bearer"));

        var act = async () => await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: null));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        harness.WriteCount.Should().Be(0);
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_CancelAfterWrite_DoesNotExecuteTheKeyedEditAgain()
    {
        var harness = new ServiceHarness(new GrpcApplyEditsReceipts(), throwAfterWrite: true);
        var user = Subject("editor", issuer: "https://issuer-a.example");

        var first = async () => await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: null));
        await first.Should().ThrowAsync<OperationCanceledException>();
        harness.WriteCount.Should().Be(1);

        var retry = async () => await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: null));
        (await retry.Should().ThrowAsync<RpcException>()).Which.Status.Detail.Should().Contain("may have committed");
        harness.WriteCount.Should().Be(1);
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_UnknownCommit_IsNotStoredAsAFailureAndIsNotExecutedAgain()
    {
        var unknown = FeatureEditResult.FailureWithUnknownCommitOutcome(
            createResults: ImmutableArray.Create(EditOperationResult.FailureWithUnknownCommitOutcome("commit acknowledgement lost")));
        var harness = new ServiceHarness(new GrpcApplyEditsReceipts(), editResult: () => unknown);
        var user = Subject("editor", issuer: "https://issuer-a.example");

        var first = await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: null));
        first.AddResults.Should().ContainSingle().Which.Success.Should().BeFalse();
        harness.WriteCount.Should().Be(1);

        var retry = async () => await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: null));
        (await retry.Should().ThrowAsync<RpcException>()).Which.Status.Detail.Should().Contain("may have committed");
        harness.WriteCount.Should().Be(1);
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_ConfirmedRollback_IsReplayedAsAFailure()
    {
        var rolledBack = FeatureEditResult.Rollback(
            createResults: ImmutableArray.Create(EditOperationResult.Failure("rolled back", objectId: 5)));
        var harness = new ServiceHarness(new GrpcApplyEditsReceipts(), editResult: () => rolledBack);
        var user = Subject("editor", issuer: "https://issuer-a.example");

        var first = await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: null));
        var retry = await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: null));

        harness.WriteCount.Should().Be(1);
        first.AddResults.Should().ContainSingle().Which.Success.Should().BeFalse();
        retry.AddResults.Should().ContainSingle().Which.Success.Should().BeFalse();
        retry.AddResults[0].ObjectId.Should().Be(first.AddResults[0].ObjectId);
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_KnownPartialSuccess_IsReplayedInsteadOfExecutedAgain()
    {
        var partial = FeatureEditResult.Success(
            createdCount: 1,
            updatedCount: 0,
            deletedCount: 0,
            createResults: ImmutableArray.Create(EditOperationResult.Success(50)),
            updateResults: ImmutableArray.Create(EditOperationResult.FailureWithUnknownCommitOutcome("later row")));
        var harness = new ServiceHarness(new GrpcApplyEditsReceipts(), editResult: () => partial);
        var user = Subject("editor", issuer: "https://issuer-a.example");

        var first = await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: null));
        var retry = await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: null));

        harness.WriteCount.Should().Be(1);
        retry.AddResults.Should().ContainSingle().Which.ObjectId.Should().Be(first.AddResults[0].ObjectId);
        retry.AddResults[0].ObjectId.Should().Be(50);
    }

    [UnitTest]
    public async Task ConcurrentCallersOnOneScope_ShareOneGate_AndReleaseItAfterwards()
    {
        using var store = new GrpcApplyEditsReceipts();

        var first = await store.EnterAsync("scope", CancellationToken.None);
        var second = store.EnterAsync("scope", CancellationToken.None);
        await Task.Delay(100);
        second.IsCompleted.Should().BeFalse("a second caller on the same scope waits for the first");
        store.GateCount.Should().Be(1);

        first.Complete(Response(objectId: 7));
        first.Dispose();
        using (var replay = await second.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            replay.Response!.AddResults[0].ObjectId.Should().Be(7);
        }

        store.GateCount.Should().Be(0);
    }

    [UnitTest]
    public async Task LocalResults_AreBoundedByTheirSizeBudget()
    {
        const long budgetBytes = 4096;
        using var store = new GrpcApplyEditsReceipts(
            durableReceipts: null,
            new GrpcApplyEditsReceipts.LocalResultLimits(GrpcApplyEditsReceipts.DefaultResponseWindow, budgetBytes));

        for (var i = 0; i < 200; i++)
        {
            using var entry = await store.EnterAsync($"scope-{i}", CancellationToken.None);
            entry.Complete(Response(objectId: i));
        }

        store.LocalResponseCount.Should().BeLessThanOrEqualTo((int)(budgetBytes / 256));
        store.GateCount.Should().Be(0);
    }

    [UnitTest]
    public async Task LocalResults_ExpireAfterTheirWindow_AndAreReclaimed()
    {
        using var store = new GrpcApplyEditsReceipts(
            durableReceipts: null,
            new GrpcApplyEditsReceipts.LocalResultLimits(
                TimeSpan.FromMilliseconds(200), GrpcApplyEditsReceipts.DefaultLocalResponseBudgetBytes));

        using (var entry = await store.EnterAsync("expiring", CancellationToken.None))
        {
            entry.Complete(Response(objectId: 1));
        }

        store.LocalResponseCount.Should().Be(1);
        await Task.Delay(500);

        using (var other = await store.EnterAsync("unrelated", CancellationToken.None))
        {
            other.Response.Should().BeNull();
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (store.LocalResponseCount > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        store.LocalResponseCount.Should().Be(0, "an expired result must be reclaimed without being requested again");
        using var retry = await store.EnterAsync("expiring", CancellationToken.None);
        retry.Response.Should().BeNull();
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task CommittedReceipt_IsReplayedOnAnotherNode_WithoutExecutingAgain()
    {
        var receipts = new SharedEditReceipts();
        var transaction = new RecordingEditTransaction();
        var wroteBeforeCommit = false;
        using var storeA = new GrpcApplyEditsReceipts(receipts);
        using var storeB = new GrpcApplyEditsReceipts(receipts);
        var nodeA = new ServiceHarness(
            storeA,
            commitReceipt: (receipt, result, cancellationToken) => CommitThroughWriterAsync(
                new ReceiptCommit(transaction, receipts, () => wroteBeforeCommit = !transaction.Committed),
                receipt,
                result,
                cancellationToken));
        var nodeB = new ServiceHarness(storeB);
        var user = Subject("editor", issuer: "https://issuer-a.example");

        var original = await nodeA.Service.ApplyEdits(AddRequest(), nodeA.Context(user, tenantId: "tenant-a"));
        var retry = await nodeB.Service.ApplyEdits(AddRequest(), nodeB.Context(user, tenantId: "tenant-a"));

        wroteBeforeCommit.Should().BeTrue("the receipt row is written on the edit transaction before commit");
        transaction.CommitCalls.Should().Be(1);
        nodeA.WriteCount.Should().Be(1);
        nodeB.WriteCount.Should().Be(0, "a committed receipt is replayed instead of executing the edit again");
        retry.AddResults.Should().ContainSingle().Which.ObjectId.Should().Be(original.AddResults[0].ObjectId);
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task DuplicateCommit_IsRolledBackByTheReceiptKey_AndReplaysTheFirstReceipt()
    {
        var receipts = new SharedEditReceipts();
        var user = Subject("editor", issuer: "https://issuer-a.example");
        using var storeA = new GrpcApplyEditsReceipts(receipts);
        using var storeB = new GrpcApplyEditsReceipts(receipts);
        var nodeA = new ServiceHarness(
            storeA,
            commitReceipt: (receipt, result, cancellationToken) => CommitThroughWriterAsync(
                new ReceiptCommit(new RecordingEditTransaction(), receipts), receipt, result, cancellationToken));
        Proto.ApplyEditsResponse? original = null;
        var losingTransaction = new RecordingEditTransaction();
        var nodeB = new ServiceHarness(
            storeB,
            commitReceipt: async (receipt, result, cancellationToken) =>
            {
                // Node B already found no receipt; node A commits the same key before B's receipt insert.
                original = await nodeA.Service.ApplyEdits(AddRequest(), nodeA.Context(user, tenantId: null));
                return await CommitThroughWriterAsync(
                    new ReceiptCommit(losingTransaction, receipts), receipt, result, cancellationToken);
            });

        var replayed = await nodeB.Service.ApplyEdits(AddRequest(), nodeB.Context(user, tenantId: null));

        losingTransaction.CommitCalls.Should().Be(0, "the receipt key rolls the duplicate commit back");
        replayed.AddResults.Should().ContainSingle().Which.ObjectId.Should().Be(original!.AddResults[0].ObjectId);
        storeB.GateCount.Should().Be(0);
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task SupersededCommit_WhoseReceiptCannotBeRead_IsRefusedAndNotExecutedAgain()
    {
        var harness = new ServiceHarness(
            new GrpcApplyEditsReceipts(),
            commitReceipt: (_, result, _) => Task.FromResult(new FeatureEditApplication
            {
                Result = result,
                SupersededByExistingReceipt = true,
            }));
        var user = Subject("editor", issuer: "https://issuer-a.example");

        var first = async () => await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: null));
        var retry = async () => await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: null));

        (await first.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Aborted);
        (await retry.Should().ThrowAsync<RpcException>()).Which.Status.Detail.Should().Contain("may have committed");
        harness.WriteCount.Should().Be(1);
    }

    private sealed record ReceiptCommit(
        RecordingEditTransaction Transaction,
        SharedEditReceipts Receipts,
        Action? OnWrite = null);

    private static async Task<FeatureEditApplication> CommitThroughWriterAsync(
        ReceiptCommit commit,
        FeatureEditIdempotencyReceipt receipt,
        FeatureEditResult result,
        CancellationToken cancellationToken)
    {
        var staged = await FeatureDataAccess.CommitStagedEditAsync(
            commit.Transaction,
            result,
            receipt,
            (editTransaction, scope, payload, _, _) =>
            {
                editTransaction.Should().BeSameAs(commit.Transaction);
                commit.OnWrite?.Invoke();
                return Task.FromResult(commit.Receipts.TryInsert(scope, payload.ToArray()));
            },
            cancellationToken).ConfigureAwait(false);
        return new FeatureEditApplication
        {
            Result = result,
            ReceiptDurable = staged.ReceiptDurable,
            SupersededByExistingReceipt = staged.SupersededByExistingReceipt,
        };
    }

    private sealed class SharedEditReceipts : IFeatureEditIdempotencyReceipts
    {
        private readonly ConcurrentDictionary<string, byte[]> _rows = new(StringComparer.Ordinal);

        public bool TryInsert(string scope, byte[] payload) => _rows.TryAdd(scope, payload);

        public ValueTask<ReadOnlyMemory<byte>?> TryReadAsync(string scope, CancellationToken cancellationToken = default)
            => new(_rows.TryGetValue(scope, out var payload) ? payload : null);
    }

    private sealed class RecordingEditTransaction : DbTransaction
    {
        public int CommitCalls { get; private set; }

        public bool Committed => CommitCalls > 0;

        public override IsolationLevel IsolationLevel => IsolationLevel.RepeatableRead;

        protected override DbConnection? DbConnection => null;

        public override void Commit() => CommitAsync().GetAwaiter().GetResult();

        public override Task CommitAsync(CancellationToken cancellationToken = default)
        {
            CommitCalls++;
            return Task.CompletedTask;
        }

        public override void Rollback()
        {
        }

        public override Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal static Proto.ApplyEditsResponse Response(long objectId)
    {
        var response = new Proto.ApplyEditsResponse();
        response.AddResults.Add(new Proto.EditResult { ObjectId = objectId, Success = true });
        return response;
    }

    internal static ClaimsPrincipal Subject(string subject, string? issuer, string scheme = "Bearer")
    {
        var claims = new List<Claim>
        {
            new("sub", subject),
            new(ClaimTypes.Role, "admin"),
        };
        if (issuer is not null)
        {
            claims.Add(new Claim("iss", issuer));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }

    internal static Proto.ApplyEditsRequest AddRequest(string key = ClientKey)
    {
        var request = new Proto.ApplyEditsRequest
        {
            ServiceId = "test",
            LayerId = 0,
            RollbackOnFailure = true,
            IdempotencyKey = key,
        };
        request.Adds.Add(new Proto.Feature
        {
            Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "created" } },
        });
        return request;
    }

    // One server node: a real HonuaFeatureService with its own in-process state.
    internal sealed class ServiceHarness
    {
        private static long _nextObjectId = 1000;
        private readonly StrongBox<int> _writeCount = new();

        public ServiceHarness(
            GrpcApplyEditsReceipts store,
            Func<FeatureEditResult>? editResult = null,
            bool throwAfterWrite = false,
            Func<FeatureEditIdempotencyReceipt, FeatureEditResult, CancellationToken, Task<FeatureEditApplication>>? commitReceipt = null)
        {
            Service = CreateService(
                store,
                Substitute.For<IFeatureReader>(),
                ScriptedWriter(_writeCount, new ScriptedWrites(editResult, throwAfterWrite, commitReceipt)));
        }

        public ServiceHarness(GrpcApplyEditsReceipts store, IFeatureReader reader, IFeatureWriter writer)
        {
            Service = CreateService(store, reader, writer);
        }

        public HonuaFeatureService Service { get; }

        public int WriteCount => Volatile.Read(ref _writeCount.Value);

        private sealed record ScriptedWrites(
            Func<FeatureEditResult>? EditResult,
            bool ThrowAfterWrite,
            Func<FeatureEditIdempotencyReceipt, FeatureEditResult, CancellationToken, Task<FeatureEditApplication>>? CommitReceipt);

        private static IFeatureWriter ScriptedWriter(StrongBox<int> writeCount, ScriptedWrites script)
        {
            FeatureEditResult Write(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref writeCount.Value);
                if (script.ThrowAfterWrite)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (script.EditResult is not null)
                {
                    return script.EditResult();
                }

                var objectId = Interlocked.Increment(ref _nextObjectId);
                return FeatureEditResult.Success(
                    createdCount: 1,
                    updatedCount: 0,
                    deletedCount: 0,
                    createResults: ImmutableArray.Create(EditOperationResult.Success(objectId)));
            }

            var writer = Substitute.For<IFeatureWriter>();
            writer
                .ApplyEditsAsync(default, default, default)
                .ReturnsForAnyArgs(call => Write(call.ArgAt<CancellationToken>(2)));
            writer
                .ApplyEditsAsync(default, default, default(FeatureEditIdempotencyReceipt), default)
                .ReturnsForAnyArgs(async call =>
                {
                    var cancellationToken = call.ArgAt<CancellationToken>(3);
                    var result = Write(cancellationToken);
                    if (script.CommitReceipt is null)
                    {
                        return new FeatureEditApplication { Result = result, ReceiptDurable = false };
                    }

                    return await script.CommitReceipt(
                        call.ArgAt<FeatureEditIdempotencyReceipt>(2),
                        result,
                        cancellationToken).ConfigureAwait(false);
                });
            return writer;
        }

        private static HonuaFeatureService CreateService(
            GrpcApplyEditsReceipts store,
            IFeatureReader reader,
            IFeatureWriter writer)
        {
            var resourceValidator = Substitute.For<IResourceValidator>();
            resourceValidator
                .ValidateServiceLayerV2Async("test", 0, Arg.Any<CancellationToken>())
                .Returns(ResourceValidationResult.Success(CreateTriple(CreateServiceMetadata(), CreateResource())));

            var crsRegistry = Substitute.For<ICrsRegistry>();
#pragma warning disable CA2012 // NSubstitute setup for ValueTask-returning members.
            crsRegistry
                .IsSridSupportedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(_ => new ValueTask<bool>(true));
#pragma warning restore CA2012

            return new HonuaFeatureService(
                resourceValidator,
                reader,
                writer,
                Substitute.For<IStreamingFeatureStore>(),
                new CommonQueryValidator(Options.Create(new LimitsOptions())),
                new SpatialReferenceResolver(Substitute.For<ICrsDetectionService>(), crsRegistry),
                new FeatureMutationEventService(
                    Substitute.For<IFeatureChangeEventPublisher>(),
                    outboxCapabilityProvider: Substitute.For<IOutboxCapabilityProvider>()),
                Options.Create(new LimitsOptions()),
                Options.Create(new GrpcOptions()),
                NullLogger<HonuaFeatureService>.Instance,
                store);
        }

        public ServerCallContext Context(ClaimsPrincipal user, string? tenantId)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
            var tenantContext = Substitute.For<ITenantContext>();
            tenantContext.TenantId.Returns(tenantId);
            services.AddSingleton(tenantContext);
            services.Configure<RbacOptions>(_ => { });

            var context = new TestServerCallContext();
            context.UserState["__HttpContext"] = new DefaultHttpContext
            {
                RequestServices = services.BuildServiceProvider(),
                User = user,
            };
            return context;
        }

        private static MetadataV2Service CreateServiceMetadata() => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-test", Name = "test" },
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            Protocols = ["Grpc"],
        };

        private static MetadataV2Resource CreateResource() => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-test", Name = "test" },
            Spatial = new MetadataV2ResourceSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                SpatialReference = MetadataV2SpatialReference.Wgs84,
            },
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "objectid",
                    Type = MetadataV2FieldType.Integer,
                    Nullable = false,
                    SemanticRoles = ["id.primary"],
                },
                new MetadataV2Field
                {
                    Name = "name",
                    Type = MetadataV2FieldType.String,
                    Length = 255,
                    Nullable = true,
                },
            ],
        };

        private static MetadataV2ServiceLayerTriple CreateTriple(
            MetadataV2Service service,
            MetadataV2Resource resource)
            => new(
                service,
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "publication-test", Name = resource.Metadata.Name },
                    ServiceId = service.Metadata.Id,
                    ResourceId = resource.Metadata.Id,
                    Identifier = new MetadataV2PublicationIdentifier { Value = "0", IsNumeric = true },
                    IsPrimary = true,
                },
                resource)
            {
                StorageLayerId = 0,
            };
    }

    private sealed class TestServerCallContext : ServerCallContext
    {
        protected override string MethodCore => "/geospatial.v1.FeatureService/ApplyEdits";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(5);
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
