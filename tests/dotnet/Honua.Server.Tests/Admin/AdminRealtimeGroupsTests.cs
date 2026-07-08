// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Server.Tests.Admin;

/// <summary>
/// Realtime ops-push coverage for #2554: the ops-health / operate-events / deploy-operations hub groups,
/// their admin authorization, the backplane feature-detect fallback, and real cross-replica fan-out over a
/// Testcontainers Redis backplane.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Streaming)]
public sealed class AdminRealtimeGroupsTests : IClassFixture<RedisFixture>
{
    private const string AdminPassword = "admin-realtime-groups-password";
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly RedisFixture _redis;

    public AdminRealtimeGroupsTests(RedisFixture redis)
    {
        _redis = redis;
    }

    [IntegrationTest]
    [Endpoint("POST /hubs/admin/negotiate")]
    public async Task NonAdminConnection_CannotConnect_SoCannotSubscribe()
    {
        using var factory = CreateFactory(redisConnectionString: null);
        await using var connection = BuildConnection(factory, apiKey: null);

        // Hub-level admin authorization gates the whole connection: a non-admin cannot negotiate, therefore
        // cannot reach any Subscribe method.
        var connect = async () => await connection.StartAsync();
        await connect.Should().ThrowAsync<Exception>();
    }

    [IntegrationTest]
    [Endpoint("POST /hubs/admin/negotiate")]
    public async Task WithoutBackplane_GroupsNotAdvertised_AndSubscribeRejected()
    {
        using var factory = CreateFactory(redisConnectionString: null);
        await using var connection = BuildConnection(factory, AdminPassword);
        await connection.StartAsync();

        var status = await connection.InvokeAsync<JsonElement>("GetStatus");
        var probe = Deserialize<StatusProbe>(status);
        probe.BackplaneEnabled.Should().BeFalse();
        probe.RealtimeGroups.Should().BeEmpty();

        // Feature-detect contract: with no backplane the group is not joinable — the client must poll.
        var subscribe = async () => await connection.InvokeAsync("SubscribeToOpsHealth");
        await subscribe.Should().ThrowAsync<HubException>();
    }

    [IntegrationTest]
    [Endpoint("POST /hubs/admin/negotiate")]
    public async Task WithBackplane_OpsHealthFlush_PushesSnapshotToSubscriber()
    {
        using var factory = CreateFactory(_redis.ConnectionString);
        await using var connection = BuildConnection(factory, AdminPassword);

        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>(
            AdminRealtimeContract.OpsHealthSnapshotEventName,
            snapshot => received.TrySetResult(snapshot));

        await connection.StartAsync();

        var status = Deserialize<StatusProbe>(await connection.InvokeAsync<JsonElement>("GetStatus"));
        status.BackplaneEnabled.Should().BeTrue();
        status.RealtimeGroups.Should().Contain(AdminRealtimeContract.OpsHealthGroup);

        await connection.InvokeAsync("SubscribeToOpsHealth");

        var flushSignal = factory.Services.GetRequiredService<OpsHealthFlushSignal>();
        flushSignal.Raise(BuildSnapshot("Degraded"));

        var payload = await received.Task.WaitAsync(ReceiveTimeout);
        payload.GetProperty("overallStatus").GetString().Should().Be("Degraded");
    }

    [IntegrationTest]
    [Endpoint("POST /hubs/admin/negotiate")]
    public async Task WithBackplane_Transition_PushesDeployAndOperateEvents()
    {
        using var factory = CreateFactory(_redis.ConnectionString);
        await using var connection = BuildConnection(factory, AdminPassword);

        var deploy = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>(AdminRealtimeContract.DeployOperationEventName, e => deploy.TrySetResult(e));
        connection.On<JsonElement>(AdminRealtimeContract.OperateEventEventName, e => operate.TrySetResult(e));

        await connection.StartAsync();
        await connection.InvokeAsync("SubscribeToDeployOperations");
        await connection.InvokeAsync("SubscribeToOperateEvents");

        var operationId = $"op-{Guid.NewGuid():N}";
        var listener = factory.Services.GetServices<IWorkflowOperationTransitionListener>()
            .OfType<RealtimeOperationTransitionListener>()
            .Single();
        await listener.OnTransitionAsync(
            BuildTransition(operationId, WorkflowOperationTransitionKind.Submitted));

        var deployPayload = await deploy.Task.WaitAsync(ReceiveTimeout);
        deployPayload.GetProperty("operationId").GetString().Should().Be(operationId);
        deployPayload.GetProperty("transitionKind").GetString().Should().Be("Submitted");
        deployPayload.GetProperty("eventId").GetString().Should().NotBeNullOrWhiteSpace();

        var operatePayload = await operate.Task.WaitAsync(ReceiveTimeout);
        operatePayload.GetProperty("kind").GetString().Should().Be("release");
        operatePayload.GetProperty("operationId").GetString().Should().Be(operationId);
        operatePayload.GetProperty("title").GetString().Should().StartWith("Deploy submitted");
    }

    [IntegrationTest]
    [Endpoint("POST /hubs/admin/negotiate")]
    public async Task WithBackplane_FansOutAcrossReplicas()
    {
        // Two independent server hosts sharing ONE Redis backplane: a client connected to host B must receive
        // an ops-health snapshot produced on host A. This is the multi-replica fan-out the ticket exists for.
        using var hostA = CreateFactory(_redis.ConnectionString);
        using var hostB = CreateFactory(_redis.ConnectionString);
        await using var connectionB = BuildConnection(hostB, AdminPassword);

        var marker = $"FanOut-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connectionB.On<JsonElement>(AdminRealtimeContract.OpsHealthSnapshotEventName, snapshot =>
        {
            if (snapshot.GetProperty("overallStatus").GetString() == marker)
            {
                received.TrySetResult(snapshot);
            }
        });

        await connectionB.StartAsync();
        await connectionB.InvokeAsync("SubscribeToOpsHealth");

        // Produce on host A; host B's connected client should receive it via the shared backplane.
        var flushOnA = hostA.Services.GetRequiredService<OpsHealthFlushSignal>();
        flushOnA.Raise(BuildSnapshot(marker));

        var payload = await received.Task.WaitAsync(ReceiveTimeout);
        payload.GetProperty("overallStatus").GetString().Should().Be(marker);
    }

    private static WebApplicationFactory<Program> CreateFactory(string? redisConnectionString)
        => new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                // Set the Redis connection via UseSetting (not ConfigureAppConfiguration) so it is visible in
                // builder.Configuration when AddAdminRealtime reads it during service registration — app
                // configuration sources are only merged at build time, which is too late for the backplane gate.
                if (!string.IsNullOrWhiteSpace(redisConnectionString))
                {
                    builder.UseSetting("ConnectionStrings:redis", redisConnectionString);
                }

                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["HONUA_ADMIN_PASSWORD"] = AdminPassword,
                    });
                });
            });

    private static HubConnection BuildConnection(WebApplicationFactory<Program> factory, string? apiKey)
    {
        var server = factory.Server;
        var builder = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, AdminRealtimeContract.HubPath.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (apiKey is not null)
                {
                    options.Headers["X-API-Key"] = apiKey;
                }
            });

        return builder.Build();
    }

    private static T Deserialize<T>(JsonElement element)
        => element.Deserialize<T>(CaseInsensitive)!;

    private static WorkflowOperationTransition BuildTransition(string operationId, WorkflowOperationTransitionKind kind)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new WorkflowOperationRecord
        {
            OperationId = operationId,
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Submitted,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Submitting",
        };

        return new WorkflowOperationTransition
        {
            Operation = record,
            Kind = kind,
            OccurredAt = now,
        };
    }

    private static OpsHealthSnapshotResponse BuildSnapshot(string overallStatus)
        => new()
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            OverallStatus = overallStatus,
            Health = new OpsHealthChecksView
            {
                Status = overallStatus,
                TotalDurationMs = 0,
                Entries = Array.Empty<OpsHealthCheckEntryView>(),
            },
            ServingLatency = new OpsServingLatencyView
            {
                WindowSeconds = 60,
                Protocols = Array.Empty<OpsServingLatencyProtocolView>(),
            },
            Geoprocessing = new OpsGpQueueView
            {
                TotalActive = 0,
                Available = true,
                Buckets = Array.Empty<OpsGpQueueBucketView>(),
            },
            AlertDispatch = new OpsAlertDispatchView
            {
                DispatcherRunning = false,
                DispatcherEnabled = false,
                StoragePollFailing = false,
            },
            Deploy = new OpsDeployReadinessView
            {
                Status = "ready",
                ReadyForCoordinatedDeploy = true,
                PendingMigrationsCount = 0,
                PendingContractScriptsCount = 0,
                PlatformRelease = new OpsPlatformReleaseView
                {
                    ReleaseDeclared = false,
                    IsCoVersioned = true,
                    SkewedIds = Array.Empty<string>(),
                },
            },
            Database = new OpsDatabaseView
            {
                HasConnectionPoolData = false,
                ActiveConnections = 0,
                ConnectionAcquisitionTimeouts = 0,
                ConnectionAcquisitionFailures = 0,
                CacheHitRatio = 0,
                ErrorRate = 0,
            },
        };

    private sealed record StatusProbe(
        [property: JsonPropertyName("backplaneEnabled")] bool BackplaneEnabled,
        [property: JsonPropertyName("realtimeGroups")] IReadOnlyList<string> RealtimeGroups);
}
