// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
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
using StackExchange.Redis;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

/// <summary>
/// Keyed gRPC ApplyEdits retries: a stored result is replayed only to the same
/// issuer-qualified actor in the same effective tenant (SEC-34), and a keyed edit
/// commits at most once across replicas (SEC-36).
/// </summary>
[Protocol(TestProtocols.Grpc)]
[Operation(Operations.ApplyEdits)]
public sealed class GrpcApplyEditsIdempotencyTests
{
    private const string ClientKey = "client-retry-key";

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_SameActorTenantAndKey_ReplaysTheFirstResult()
    {
        var harness = new ServiceHarness(new GrpcApplyEditsIdempotencyStore());
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
        var harness = new ServiceHarness(new GrpcApplyEditsIdempotencyStore());
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
        var harness = new ServiceHarness(new GrpcApplyEditsIdempotencyStore());

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
        var harness = new ServiceHarness(new GrpcApplyEditsIdempotencyStore());

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
        using var store = new GrpcApplyEditsIdempotencyStore();
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
        var harness = new ServiceHarness(new GrpcApplyEditsIdempotencyStore());
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "Bearer"));

        var act = async () => await harness.Service.ApplyEdits(AddRequest(), harness.Context(user, tenantId: null));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        harness.WriteCount.Should().Be(0);
    }

    [UnitTest]
    public async Task ConcurrentCallersOnOneScope_ShareOneGate_AndReleaseItAfterwards()
    {
        using var store = new GrpcApplyEditsIdempotencyStore();

        var first = await store.EnterAsync("scope", CancellationToken.None);
        var second = store.EnterAsync("scope", CancellationToken.None);
        await Task.Delay(100);
        second.IsCompleted.Should().BeFalse("a second caller on the same scope waits for the first");
        store.GateCount.Should().Be(1);

        await first.CompleteAsync(Response(objectId: 7));
        await first.DisposeAsync();
        await using (var replay = await second.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            replay.Response!.AddResults[0].ObjectId.Should().Be(7);
        }

        store.GateCount.Should().Be(0);
    }

    [UnitTest]
    public async Task LocalResults_AreBoundedByTheirSizeBudget()
    {
        const long budgetBytes = 4096;
        using var store = new GrpcApplyEditsIdempotencyStore(
            multiplexer: null,
            logger: null,
            GrpcApplyEditsIdempotencyStore.DefaultReservationWindow,
            GrpcApplyEditsIdempotencyStore.DefaultResponseWindow,
            budgetBytes);

        for (var i = 0; i < 200; i++)
        {
            await using var lease = await store.EnterAsync($"scope-{i}", CancellationToken.None);
            await lease.CompleteAsync(Response(objectId: i));
        }

        store.LocalResponseCount.Should().BeLessThanOrEqualTo((int)(budgetBytes / 256));
        store.GateCount.Should().Be(0);
    }

    [UnitTest]
    public async Task LocalResults_ExpireAfterTheirWindow_AndAreReclaimed()
    {
        using var store = new GrpcApplyEditsIdempotencyStore(
            multiplexer: null,
            logger: null,
            GrpcApplyEditsIdempotencyStore.DefaultReservationWindow,
            responseWindow: TimeSpan.FromMilliseconds(200),
            GrpcApplyEditsIdempotencyStore.DefaultLocalResponseBudgetBytes);

        await using (var lease = await store.EnterAsync("expiring", CancellationToken.None))
        {
            await lease.CompleteAsync(Response(objectId: 1));
        }

        store.LocalResponseCount.Should().Be(1);
        await Task.Delay(500);

        await using (var other = await store.EnterAsync("unrelated", CancellationToken.None))
        {
            other.Response.Should().BeNull();
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (store.LocalResponseCount > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        store.LocalResponseCount.Should().Be(0, "an expired result must be reclaimed without being requested again");
        await using var retry = await store.EnterAsync("expiring", CancellationToken.None);
        retry.Response.Should().BeNull();
    }

    [UnitTest]
    public async Task ResultsAwaitingRedis_AreBoundedByCount_AndStayReplayableLocally()
    {
        var database = Substitute.For<IDatabase>();
        database
            .ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var script = call.ArgAt<string>(0);
                if (ReferenceEquals(script, GrpcApplyEditsIdempotencyStore.AcquireScript))
                {
                    return Task.FromResult(RedisResult.Create(call.ArgAt<RedisValue[]>(2)[0]));
                }

                return ReferenceEquals(script, GrpcApplyEditsIdempotencyStore.CompleteScript)
                    ? Task.FromException<RedisResult>(
                        new RedisConnectionException(ConnectionFailureType.SocketFailure, "unavailable"))
                    : Task.FromResult(RedisResult.Create((RedisValue)1));
            });
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        using var store = new GrpcApplyEditsIdempotencyStore(
            connection,
            logger: null,
            GrpcApplyEditsIdempotencyStore.DefaultReservationWindow,
            GrpcApplyEditsIdempotencyStore.DefaultResponseWindow,
            GrpcApplyEditsIdempotencyStore.DefaultLocalResponseBudgetBytes,
            maxUnpublishedResults: 4);

        for (var i = 0; i < 20; i++)
        {
            await using var lease = await store.EnterAsync($"scope-{i}", CancellationToken.None);
            (await lease.TryBeginWriteAsync(CancellationToken.None)).Should().NotBeNull();
            await lease.CompleteAsync(Response(objectId: i));
        }

        store.UnpublishedResultCount.Should().BeLessThanOrEqualTo(4);
        store.LocalResponseCount.Should().Be(20, "every committed result stays replayable on this replica");
        store.GateCount.Should().Be(0);
    }

    [UnitTheory]
    [InlineData(4, 65536)]
    [InlineData(128, 64)]
    public async Task ConcurrentFailedCompletions_RespectQueueCountAndByteBudgets(int maxResults, long localBudgetBytes)
    {
        var database = Substitute.For<IDatabase>();
        database
            .ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var script = call.ArgAt<string>(0);
                if (ReferenceEquals(script, GrpcApplyEditsIdempotencyStore.AcquireScript))
                {
                    return Task.FromResult(RedisResult.Create(call.ArgAt<RedisValue[]>(2)[0]));
                }

                return ReferenceEquals(script, GrpcApplyEditsIdempotencyStore.CompleteScript)
                    ? Task.FromException<RedisResult>(
                        new RedisConnectionException(ConnectionFailureType.SocketFailure, "unavailable"))
                    : Task.FromResult(RedisResult.Create((RedisValue)1));
            });
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        var response = Response(objectId: 7);
        var receiptBytes = response.CalculateSize() + 1;
        var expectedLimit = Math.Min(maxResults, (int)(localBudgetBytes / 4 / receiptBytes));

        for (var round = 0; round < 10; round++)
        {
            using var store = new GrpcApplyEditsIdempotencyStore(
                connection, logger: null,
                GrpcApplyEditsIdempotencyStore.DefaultReservationWindow,
                GrpcApplyEditsIdempotencyStore.DefaultResponseWindow,
                localBudgetBytes, maxResults);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completions = Enumerable.Range(0, 64).Select(async i =>
            {
                await using var lease = await store.EnterAsync($"scope-{i}", CancellationToken.None);
                await start.Task;
                await lease.CompleteAsync(response);
            }).ToArray();

            start.SetResult();
            await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(30));

            store.UnpublishedResultCount.Should().Be(expectedLimit, "concurrent producers must honor both limits");
            store.GateCount.Should().Be(0);
        }
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

    /// <summary>One server replica: a real <see cref="HonuaFeatureService"/> over substitute storage.</summary>
    internal sealed class ServiceHarness
    {
        private static long _nextObjectId = 1000;
        private int _writeCount;

        public ServiceHarness(
            GrpcApplyEditsIdempotencyStore store,
            Func<CancellationToken, Task>? beforeCommit = null)
        {
            var resourceValidator = Substitute.For<IResourceValidator>();
            var service = CreateService();
            var resource = CreateResource();
            resourceValidator
                .ValidateServiceLayerV2Async("test", 0, Arg.Any<CancellationToken>())
                .Returns(ResourceValidationResult.Success(CreateTriple(service, resource)));

            var writer = Substitute.For<IFeatureWriter>();
            writer
                .ApplyEditsAsync(default, default, default)
                .ReturnsForAnyArgs(async call =>
                {
                    var cancellationToken = call.ArgAt<CancellationToken>(2);
                    if (beforeCommit is not null)
                    {
                        await beforeCommit(cancellationToken);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref _writeCount);
                    var objectId = Interlocked.Increment(ref _nextObjectId);
                    return FeatureEditResult.Success(
                        createdCount: 1,
                        updatedCount: 0,
                        deletedCount: 0,
                        createResults: ImmutableArray.Create(EditOperationResult.Success(objectId)));
                });

            var crsRegistry = Substitute.For<ICrsRegistry>();
#pragma warning disable CA2012 // NSubstitute setup for ValueTask-returning members.
            crsRegistry
                .IsSridSupportedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(_ => new ValueTask<bool>(true));
#pragma warning restore CA2012

            Service = new HonuaFeatureService(
                resourceValidator,
                Substitute.For<IFeatureReader>(),
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

        public HonuaFeatureService Service { get; }

        public int WriteCount => Volatile.Read(ref _writeCount);

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

        private static MetadataV2Service CreateService() => new()
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

/// <summary>
/// Redis-backed replicas sharing one reservation namespace: a keyed edit commits at most
/// once even when its reservation lapses while the first writer is still running (SEC-36).
/// </summary>
[Protocol(TestProtocols.TestQuality)]
[Collection(RedisFixture.CollectionName)]
public sealed class GrpcApplyEditsDistributedIdempotencyTests(RedisFixture redis) : IAsyncLifetime
{
    private const string RedisPrefix = "honua:grpc:apply-edits:idempotency:";
    private readonly List<ConnectionMultiplexer> _connections = [];

    public Task InitializeAsync() => ClearReservationsAsync();

    public async Task DisposeAsync()
    {
        await ClearReservationsAsync();
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReservationLapsedDuringWrite_SecondReplicaCommits_FirstWriterDoesNotCommit()
    {
        var firstWriterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replicaA = new GrpcApplyEditsIdempotencyTests.ServiceHarness(
            new GrpcApplyEditsIdempotencyStore(await ConnectAsync()),
            beforeCommit: async cancellationToken =>
            {
                firstWriterStarted.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            });
        var replicaB = new GrpcApplyEditsIdempotencyTests.ServiceHarness(
            new GrpcApplyEditsIdempotencyStore(await ConnectAsync()));
        var user = GrpcApplyEditsIdempotencyTests.Subject("editor", issuer: "https://issuer-a.example");

        var first = replicaA.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), replicaA.Context(user, tenantId: null));
        await firstWriterStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The reservation disappears while the first writer is still running, exactly as
        // it would once its time-to-live elapsed.
        (await ClearReservationsAsync()).Should().BeGreaterThan(0);

        await replicaB.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), replicaB.Context(user, tenantId: null));
        replicaB.WriteCount.Should().Be(1);

        var firstOutcome = async () => await first.WaitAsync(TimeSpan.FromSeconds(60));
        (await firstOutcome.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Aborted);
        (replicaA.WriteCount + replicaB.WriteCount).Should().Be(1, "the keyed edit must commit exactly once");
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WriteOutlastingTheReservationWindow_KeepsIt_AndAnotherReplicaReplaysTheResult()
    {
        var reservationWindow = TimeSpan.FromSeconds(1);
        var firstWriterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replicaA = new GrpcApplyEditsIdempotencyTests.ServiceHarness(
            Store(await ConnectAsync(), reservationWindow),
            beforeCommit: async cancellationToken =>
            {
                firstWriterStarted.TrySetResult();
                await Task.Delay(reservationWindow * 4, cancellationToken);
            });
        var replicaB = new GrpcApplyEditsIdempotencyTests.ServiceHarness(Store(await ConnectAsync(), reservationWindow));
        var user = GrpcApplyEditsIdempotencyTests.Subject("editor", issuer: "https://issuer-a.example");

        var first = replicaA.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), replicaA.Context(user, tenantId: null));
        await firstWriterStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var retry = await replicaB.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), replicaB.Context(user, tenantId: null));
        var original = await first.WaitAsync(TimeSpan.FromSeconds(30));

        replicaA.WriteCount.Should().Be(1);
        replicaB.WriteCount.Should().Be(0, "the reservation was renewed for as long as the first write ran");
        retry.AddResults[0].ObjectId.Should().Be(original.AddResults[0].ObjectId);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task RedisBackedResult_IsSharedAcrossReplicas_WithoutALocalCopy()
    {
        using var storeA = new GrpcApplyEditsIdempotencyStore(await ConnectAsync());
        var replicaA = new GrpcApplyEditsIdempotencyTests.ServiceHarness(storeA);
        var replicaB = new GrpcApplyEditsIdempotencyTests.ServiceHarness(new GrpcApplyEditsIdempotencyStore(await ConnectAsync()));
        var user = GrpcApplyEditsIdempotencyTests.Subject("editor", issuer: "https://issuer-a.example");

        var original = await replicaA.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), replicaA.Context(user, tenantId: "tenant-a"));
        var retry = await replicaB.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), replicaB.Context(user, tenantId: "tenant-a"));
        await replicaB.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), replicaB.Context(user, tenantId: "tenant-b"));

        retry.AddResults[0].ObjectId.Should().Be(original.AddResults[0].ObjectId);
        (replicaA.WriteCount + replicaB.WriteCount).Should().Be(2, "only the other tenant's edit executes again");
        storeA.LocalResponseCount.Should().Be(0);
        storeA.GateCount.Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ResultThatFailsToPersist_IsReplayedLocally_AndPublishedForOtherReplicas()
    {
        var reservationWindow = TimeSpan.FromSeconds(1.2);
        var real = (await ConnectAsync()).GetDatabase();
        var failedCompletions = 0;
        var flaky = Substitute.For<IDatabase>();
        flaky
            .ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var script = call.ArgAt<string>(0);
                if (ReferenceEquals(script, GrpcApplyEditsIdempotencyStore.CompleteScript)
                    && Interlocked.Increment(ref failedCompletions) == 1)
                {
                    return Task.FromException<RedisResult>(
                        new RedisConnectionException(ConnectionFailureType.SocketFailure, "connection dropped"));
                }

                return real.ScriptEvaluateAsync(
                    script, call.ArgAt<RedisKey[]?>(1), call.ArgAt<RedisValue[]?>(2), call.ArgAt<CommandFlags>(3));
            });
        var flakyConnection = Substitute.For<IConnectionMultiplexer>();
        flakyConnection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(flaky);

        using var storeA = Store(flakyConnection, reservationWindow);
        var replicaA = new GrpcApplyEditsIdempotencyTests.ServiceHarness(storeA);
        var replicaB = new GrpcApplyEditsIdempotencyTests.ServiceHarness(Store(await ConnectAsync(), reservationWindow));
        var user = GrpcApplyEditsIdempotencyTests.Subject("editor", issuer: "https://issuer-a.example");

        var original = await replicaA.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), replicaA.Context(user, tenantId: null));
        var sameReplicaRetry = await replicaA.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), replicaA.Context(user, tenantId: null));
        var otherReplicaRetry = await replicaB.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), replicaB.Context(user, tenantId: null));

        Volatile.Read(ref failedCompletions).Should().BeGreaterThan(1, "the result is published again after the failure");
        (replicaA.WriteCount + replicaB.WriteCount).Should().Be(1);
        sameReplicaRetry.AddResults[0].ObjectId.Should().Be(original.AddResults[0].ObjectId);
        otherReplicaRetry.AddResults[0].ObjectId.Should().Be(original.AddResults[0].ObjectId);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task RedisOutageOutlastingTheReservation_AfterCommit_DoesNotLetAnotherReplicaExecuteAgain()
    {
        var reservationWindow = TimeSpan.FromSeconds(1);
        var real = (await ConnectAsync()).GetDatabase();
        var outage = 0;
        var flaky = Substitute.For<IDatabase>();
        flaky
            .ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(call => Volatile.Read(ref outage) == 1
                ? Task.FromException<RedisResult>(
                    new RedisConnectionException(ConnectionFailureType.SocketFailure, "redis unreachable"))
                : real.ScriptEvaluateAsync(
                    call.ArgAt<string>(0), call.ArgAt<RedisKey[]?>(1), call.ArgAt<RedisValue[]?>(2), call.ArgAt<CommandFlags>(3)));
        var flakyConnection = Substitute.For<IConnectionMultiplexer>();
        flakyConnection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(flaky);

        // Replica A loses Redis while its edit commits, so it can neither renew its key nor
        // record the result.
        using var storeA = Store(flakyConnection, reservationWindow);
        var replicaA = new GrpcApplyEditsIdempotencyTests.ServiceHarness(
            storeA,
            beforeCommit: _ =>
            {
                Volatile.Write(ref outage, 1);
                return Task.CompletedTask;
            });
        var replicaB = new GrpcApplyEditsIdempotencyTests.ServiceHarness(Store(await ConnectAsync(), reservationWindow));
        var user = GrpcApplyEditsIdempotencyTests.Subject("editor", issuer: "https://issuer-a.example");

        var original = await replicaA.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), replicaA.Context(user, tenantId: null));

        // The outage outlasts the reservation window several times over.
        await Task.Delay(reservationWindow * 3);
        var duringOutage = async () => await replicaB.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), replicaB.Context(user, tenantId: null));
        (await duringOutage.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Aborted);
        replicaB.WriteCount.Should().Be(0, "a key whose edit may have committed must not be executed again");

        // Once Redis is reachable again, replica A publishes its result and B replays it.
        Volatile.Write(ref outage, 0);
        Proto.ApplyEditsResponse? replayed = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (replayed is null && DateTime.UtcNow < deadline)
        {
            try
            {
                replayed = await replicaB.Service.ApplyEdits(
                    GrpcApplyEditsIdempotencyTests.AddRequest(), replicaB.Context(user, tenantId: null));
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Aborted)
            {
                await Task.Delay(100);
            }
        }

        replayed.Should().NotBeNull();
        replayed!.AddResults[0].ObjectId.Should().Be(original.AddResults[0].ObjectId);
        (replicaA.WriteCount + replicaB.WriteCount).Should().Be(1, "the keyed edit must commit exactly once");
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WriteThatFails_ReturnsItsKeyToAShortReservation_SoARetryExecutes()
    {
        var reservationWindow = TimeSpan.FromSeconds(1);
        var replicaA = new GrpcApplyEditsIdempotencyTests.ServiceHarness(
            Store(await ConnectAsync(), reservationWindow),
            beforeCommit: _ => throw new InvalidOperationException("storage rejected the edit"));
        var replicaB = new GrpcApplyEditsIdempotencyTests.ServiceHarness(Store(await ConnectAsync(), reservationWindow));
        var user = GrpcApplyEditsIdempotencyTests.Subject("editor", issuer: "https://issuer-a.example");

        var failed = async () => await replicaA.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), replicaA.Context(user, tenantId: null));
        await failed.Should().ThrowAsync<InvalidOperationException>();

        var retry = await replicaB.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), replicaB.Context(user, tenantId: null))
            .WaitAsync(TimeSpan.FromSeconds(15));

        retry.AddResults.Should().ContainSingle();
        replicaB.WriteCount.Should().Be(1, "a failed write must not hold its key for the whole response window");
    }

    private static GrpcApplyEditsIdempotencyStore Store(IConnectionMultiplexer connection, TimeSpan reservationWindow)
        => new(
            connection,
            logger: null,
            reservationWindow,
            GrpcApplyEditsIdempotencyStore.DefaultResponseWindow,
            GrpcApplyEditsIdempotencyStore.DefaultLocalResponseBudgetBytes);

    private async Task<IConnectionMultiplexer> ConnectAsync()
    {
        var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        _connections.Add(connection);
        return connection;
    }

    private async Task<int> ClearReservationsAsync()
    {
        var options = ConfigurationOptions.Parse(redis.ConnectionString);
        options.AllowAdmin = true;
        await using var admin = await ConnectionMultiplexer.ConnectAsync(options);
        var removed = 0;
        foreach (var endpoint in admin.GetEndPoints())
        {
            await foreach (var key in admin.GetServer(endpoint).KeysAsync(pattern: RedisPrefix + "*"))
            {
                if (await admin.GetDatabase().KeyDeleteAsync(key))
                {
                    removed++;
                }
            }
        }

        return removed;
    }
}
