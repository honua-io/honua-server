// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Realtime;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Realtime;
using Honua.Server.Tests.Infrastructure;
using Honua.Server.Tests.Realtime;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Admin;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Streaming)]
public sealed class AdminRealtimeGroupsTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private const string AdminPassword = "admin-realtime-groups-password";

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/live")]
    public async Task Live_StatusOnlyConnection_ReplaysEventsAfterDisconnect()
    {
        await using var log = await RealtimeLogHarness.CreateAsync(database);
        var dispatcher = log.CreateDispatcher();
        using var tail = log.CreateTail(dispatcher, null, new RealtimeOptions());
        await tail.StartAsync(default);
        using var host = CreateFactory(log, dispatcher);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        using var initial = await client.GetAsync("/api/v1/admin/live?topics=ops-health", HttpCompletionOption.ResponseHeadersRead);
        using var initialReader = new StreamReader(await initial.Content.ReadAsStreamAsync());
        var status = await ReadFrameAsync(initialReader);
        status.Id.Should().NotBeNullOrWhiteSpace("even a quiet stream must expose its replay boundary");
        initial.Dispose();

        await host.Services.GetRequiredService<AdminRealtimePublisher>().PublishAsync(
            new AdminRealtimeMessage(AdminRealtimeContract.OpsHealthTopic, "OpsHealthSnapshot",
                JsonSerializer.SerializeToElement(new { overallStatus = "Degraded" })), default);
        client.DefaultRequestHeaders.Add("Last-Event-ID", status.Id);
        using var resumed = await client.GetAsync("/api/v1/admin/live?topics=ops-health", HttpCompletionOption.ResponseHeadersRead);
        using var resumedReader = new StreamReader(await resumed.Content.ReadAsStreamAsync());
        (await ReadFrameAsync(resumedReader)).Kind.Should().Be("status");
        (await ReadFrameAsync(resumedReader)).Payload.GetProperty("overallStatus").GetString().Should().Be("Degraded");
        await tail.StopAsync(default);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/live")]
    public async Task Live_AnotherTaskPublishes_ReceivesHealthAndResumesTransitionsOnReplacementTask()
    {
        await using var log = await RealtimeLogHarness.CreateAsync(database);
        var dispatcherA = log.CreateDispatcher();
        var dispatcherB = log.CreateDispatcher();
        using var tailA = log.CreateTail(dispatcherA, null, new RealtimeOptions());
        using var tailB = log.CreateTail(dispatcherB, null, new RealtimeOptions());
        await tailA.StartAsync(default);
        await tailB.StartAsync(default);
        using var hostA = CreateFactory(log, dispatcherA);
        using var hostB = CreateFactory(log, dispatcherB);
        using var clientB = hostB.CreateClient();
        clientB.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        using var response = await clientB.GetAsync("/api/v1/admin/live", HttpCompletionOption.ResponseHeadersRead);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var status = await ReadFrameAsync(reader);
        status.Kind.Should().Be("status");
        status.Payload.GetProperty("topics").GetArrayLength().Should().Be(4);
        status.Payload.TryGetProperty("backplaneEnabled", out _).Should().BeFalse();
        hostA.Services.GetRequiredService<OpsHealthFlushSignal>().Raise(BuildSnapshot("Degraded"));
        var health = await ReadFrameAsync(reader);
        health.Kind.Should().Be("OpsHealthSnapshot");
        health.Payload.GetProperty("overallStatus").GetString().Should().Be("Degraded");
        health.Payload.GetProperty("taskId").GetString().Should().NotBeNullOrWhiteSpace();
        await AssertProposalDeliveryAsync(hostA, reader);
        response.Dispose();
        await AssertTransitionReplayAsync(hostA, hostB, health.Id!);
        await tailA.StopAsync(default);
        await tailB.StopAsync(default);
    }

    private static async Task AssertTransitionReplayAsync(WebApplicationFactory<Program> hostA,
        WebApplicationFactory<Program> hostB, string cursor)
    {
        var listener = hostB.Services.GetServices<IWorkflowOperationTransitionListener>()
            .OfType<RealtimeOperationTransitionListener>().Single();
        await listener.OnTransitionAsync(BuildTransition("resume-op", WorkflowOperationTransitionKind.Submitted));
        using var clientA = hostA.CreateClient();
        clientA.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        clientA.DefaultRequestHeaders.Add("Last-Event-ID", cursor);
        using var replay = await clientA.GetAsync("/api/v1/admin/live?topics=deploy-operations", HttpCompletionOption.ResponseHeadersRead);
        using var replayReader = new StreamReader(await replay.Content.ReadAsStreamAsync());
        (await ReadFrameAsync(replayReader)).Kind.Should().Be("status");
        var transition = await ReadFrameAsync(replayReader);
        transition.Kind.Should().Be("DeployOperationTransition");
        transition.Payload.GetProperty("operationId").GetString().Should().Be("resume-op");
        transition.Id.Should().NotBe(cursor);
    }

    private static async Task AssertProposalDeliveryAsync(WebApplicationFactory<Program> host, StreamReader reader)
    {
        var proposal = new OperationProposal
        {
            ProposalId = "live-proposal",
            Kind = default,
            Status = OperationProposalStatus.AwaitingApproval,
            Plan = new OperationProposalPlan { ExecutionPayload = "private-execution-payload" }
        };
        var notifier = host.Services.GetRequiredService<IProposalNotifier>();
        await notifier.NotifyPendingAsync(proposal);
        var pending = await ReadFrameAsync(reader);
        pending.Kind.Should().Be("ProposalPending");
        pending.Payload.GetProperty("proposalId").GetString().Should().Be(proposal.ProposalId);
        pending.Payload.GetRawText().Should().NotContain("private-execution-payload");
        await notifier.NotifyResolvedAsync(proposal with { Status = OperationProposalStatus.Submitted });
        (await ReadFrameAsync(reader)).Kind.Should().Be("ProposalResolved");
    }

    private static WebApplicationFactory<Program> CreateFactory(RealtimeLogHarness log, RealtimeDispatcher dispatcher)
        => new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["HONUA_ADMIN_PASSWORD"] = AdminPassword }));
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<IRealtimeFanoutProvider>(
                new PostgresRealtimeFanoutProvider(log.Log, dispatcher, new RealtimePresenceBatcher(log.Log)))));
        });

    private static async Task<SseFrame> ReadFrameAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string? id = null;
        string? kind = null;
        string? data = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal)) { id = line[4..]; }
            if (line.StartsWith("event: ", StringComparison.Ordinal)) { kind = line[7..]; }
            if (line.StartsWith("data: ", StringComparison.Ordinal)) { data = line[6..]; }
            if (line.Length == 0 && data is not null)
            {
                using var document = JsonDocument.Parse(data);
                return new SseFrame(id, kind!, document.RootElement.Clone());
            }
        }
        throw new EndOfStreamException("The SSE stream closed before an event.");
    }

    private sealed record SseFrame(string? Id, string Kind, JsonElement Payload);

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

}
