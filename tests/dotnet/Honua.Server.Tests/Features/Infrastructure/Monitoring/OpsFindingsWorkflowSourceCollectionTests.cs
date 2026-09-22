// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Alerts;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Observability.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Producer-side coverage for #4840: the findings <c>workflow_operations</c> source derives its
/// completeness and clocks from the store reads the deployment rules perform, not from the store
/// being registered. Each step builds a fresh engine (the engine is scoped) over one shared
/// collection ledger (a singleton), exactly as the host composes them.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class OpsFindingsWorkflowSourceCollectionTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_WorkflowStoreReadFails_ReportsUnavailableAndRetainsLastSuccessfulCollection()
    {
        var clock = new SettableClock(WorkflowSourceFixture.T0);
        var ledger = new OpsFindingsCollectionLedger();
        var storeDown = true;
        var store = Substitute.For<IWorkflowOperationStore>();
        store.ListActiveAsync(Arg.Any<WorkflowOperationKind?>(), Arg.Any<CancellationToken>())
            .Returns(_ => storeDown
                ? Task.FromException<IReadOnlyList<WorkflowOperationRecord>>(new InvalidOperationException("store down"))
                : Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>([]));

        // Never succeeded: no clocks at all, and the failed read does not fail the evaluation.
        var initial = WorkflowSourceFixture.WorkflowSource(
            await WorkflowSourceFixture.CreateService(store, new ControlPlaneOptions(), ledger, clock).EvaluateWithEvidenceAsync());
        Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, initial.Completeness);
        Assert.Null(initial.ObservedAt);
        Assert.Null(initial.LastSuccessfulAt);
        Assert.Equal(
            [
                EvidencePostureVocabulary.ReasonCodes.MissingObservationTime,
                EvidencePostureVocabulary.ReasonCodes.NeverSucceeded,
                EvidencePostureVocabulary.ReasonCodes.SourceUnavailable,
            ],
            initial.ReasonCodes);

        storeDown = false;
        var collectedAt = WorkflowSourceFixture.T0.AddMinutes(1);
        clock.Now = collectedAt;
        var complete = WorkflowSourceFixture.WorkflowSource(
            await WorkflowSourceFixture.CreateService(store, new ControlPlaneOptions(), ledger, clock).EvaluateWithEvidenceAsync());
        Assert.Equal(EvidencePostureVocabulary.Completeness.Complete, complete.Completeness);
        Assert.Equal(collectedAt, complete.ObservedAt);
        Assert.Equal(collectedAt, complete.LastSuccessfulAt);
        Assert.Empty(complete.ReasonCodes);

        storeDown = true;
        clock.Now = WorkflowSourceFixture.T0.AddMinutes(2);
        var outage = WorkflowSourceFixture.WorkflowSource(
            await WorkflowSourceFixture.CreateService(store, new ControlPlaneOptions(), ledger, clock).EvaluateWithEvidenceAsync());
        Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, outage.Completeness);
        Assert.Equal(collectedAt, outage.ObservedAt);
        Assert.Equal(collectedAt, outage.LastSuccessfulAt);
        Assert.Null(outage.ValidUntil);
        Assert.Equal([EvidencePostureVocabulary.ReasonCodes.SourceUnavailable], outage.ReasonCodes);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Propose_WorkflowStoreFailsAfterEarlierRead_PublishesPartialCoverageAndMakesNoGatewayCall()
    {
        var clock = new SettableClock(WorkflowSourceFixture.T0);
        var store = Substitute.For<IWorkflowOperationStore>();
        store.ListActiveAsync(Arg.Any<WorkflowOperationKind?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>([]));
        store.GetMostRecentSucceededDeployByTargetAsync(WorkflowSourceFixture.TargetA, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<WorkflowOperationRecord?>(
                WorkflowSourceFixture.SucceededDeploy(WorkflowSourceFixture.TargetA, WorkflowSourceFixture.PriorArtifact)));
        store.GetMostRecentSucceededDeployByTargetAsync(WorkflowSourceFixture.TargetB, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkflowOperationRecord?>(new InvalidOperationException("index read denied")));
        var gateway = WorkflowSourceFixture.CreateGateway();
        var service = WorkflowSourceFixture.CreateService(
            store, WorkflowSourceFixture.DeclaredReleaseOptions(), new OpsFindingsCollectionLedger(), clock, gateway);

        var evaluation = await service.EvaluateWithEvidenceAsync();

        WorkflowSourceFixture.AssertPartialTargetCoverage(WorkflowSourceFixture.WorkflowSource(evaluation), WorkflowSourceFixture.T0);
        var finding = WorkflowSourceFixture.DivergenceFinding(evaluation, WorkflowSourceFixture.TargetA);
        // The unread target is unknown, so the "no succeeded deploy = divergent" contract must not fire for it.
        Assert.DoesNotContain(evaluation.Findings, f => f.Subject.TargetId == WorkflowSourceFixture.TargetB);

        var result = await service.ProposeAsync(finding.Id);

        Assert.Equal(OpsFindingProposalStatus.Blocked, result.Status);
        Assert.Equal("evidencePostureNotActionable", result.Message);
        Assert.Empty(gateway.ReceivedCalls());
    }
}

/// <summary>
/// Real-Redis coverage for #4840. The workflow store is the production
/// <see cref="RedisWorkflowOperationStore"/> over a Testcontainers Redis; no evidence envelope is
/// injected. Proposal attempts go through both <see cref="OpsFindingsService.ProposeAsync"/> and the
/// MCP finding-proposal adapter, and every negative step is bracketed by positive steps on the same
/// finding id that do reach the gateway.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class OpsFindingsWorkflowSourceRedisTests
{
    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WorkflowOperationsSource_RedisStopsAndRestarts_CompleteThenUnavailableThenComplete()
    {
        // Docker can remap ephemeral host ports on restart; keep the endpoint stable so the same
        // multiplexer (and the same store instance) recovers.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var redisPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var container = new RedisBuilder("redis:7.2-alpine")
            .WithPortBinding(redisPort, 6379)
            .WithCommand("redis-server", "--appendonly", "yes", "--appendfsync", "always", "--save", "")
            .Build();
        await container.StartAsync();
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(OutageTolerant(container.GetConnectionString()));
        // With AbortOnConnectFail=false ConnectAsync can return while the connection is still being
        // established, and the fail-fast backlog would refuse the seed write (#5002).
        await WaitForRedisAsync(multiplexer);
        var store = new RedisWorkflowOperationStore(multiplexer, NullLogger<RedisWorkflowOperationStore>.Instance);
        Assert.True(await store.TryCreateAsync(
            WorkflowSourceFixture.SucceededDeploy(WorkflowSourceFixture.TargetA, WorkflowSourceFixture.PriorArtifact)));
        var ledger = new OpsFindingsCollectionLedger();
        var clock = new SettableClock(WorkflowSourceFixture.T0);
        var options = WorkflowSourceFixture.DeclaredReleaseOptions(WorkflowSourceFixture.TargetA);

        // Step 1: complete, clocked at the successful collection.
        var firstCollection = WorkflowSourceFixture.T0;
        var findingId = await WorkflowSourceFixture.AssertCompleteAndProposableAsync(store, options, ledger, clock, firstCollection);

        // Step 2: backend loss, evaluated twice. Unavailable, the retained clocks stay at the step-1
        // collection, and neither proposal surface reaches the gateway or the operation envelope.
        var disconnected = ObserveInteractiveConnectionFailure(multiplexer);
        await container.StopAsync();
        // Evaluate only once the multiplexer has seen the loss, so every read fails fast instead of
        // racing the stop.
        await disconnected.WaitAsync(TimeSpan.FromSeconds(30));
        foreach (var minutes in new[] { 1, 2 })
        {
            clock.Now = WorkflowSourceFixture.T0.AddMinutes(minutes);
            var gateway = WorkflowSourceFixture.CreateGateway();
            var service = WorkflowSourceFixture.CreateService(store, options, ledger, clock, gateway);

            var evaluation = await service.EvaluateWithEvidenceAsync();

            var source = WorkflowSourceFixture.WorkflowSource(evaluation);
            Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, source.Completeness);
            Assert.Equal(firstCollection, source.ObservedAt);
            Assert.Equal(firstCollection, source.LastSuccessfulAt);
            Assert.Null(source.ValidUntil);
            Assert.Equal([EvidencePostureVocabulary.ReasonCodes.SourceUnavailable], source.ReasonCodes);
            Assert.DoesNotContain(evaluation.Findings, f => f.Id == findingId);

            Assert.Equal(OpsFindingProposalStatus.FindingNotFound, (await service.ProposeAsync(findingId)).Status);
            using var services = McpPlatformOpsReaderTests.CreateServices(gateway, findings: service);
            var reader = McpPlatformOpsReaderTests.CreateReader(services: services);
            await Assert.ThrowsAsync<Honua.Geoprocessing.GeoprocessingNotFoundException>(() => reader.ProposeFindingAsync(
                McpPlatformOpsReaderTests.CreatePrincipal(),
                new McpProposeFindingArgument { FindingId = findingId, CandidateId = WorkflowSourceFixture.TargetA },
                CancellationToken.None));
            Assert.Empty(gateway.ReceivedCalls());
            await services.GetRequiredService<IOperationEnvelopeFactory>().DidNotReceive().CreateAcceptedAsync(
                Arg.Any<string>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>());
        }

        // Step 3: recovery requires a new successful collection, which advances both clocks.
        await container.StartAsync();
        Assert.Equal(redisPort, container.GetMappedPublicPort(6379));
        await WaitForRedisAsync(multiplexer);
        var recoveredAt = WorkflowSourceFixture.T0.AddMinutes(3);
        clock.Now = recoveredAt;
        var recoveredId = await WorkflowSourceFixture.AssertCompleteAndProposableAsync(store, options, ledger, clock, recoveredAt);
        Assert.Equal(findingId, recoveredId);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WorkflowOperationsSource_RedisDeniesOneTargetIndex_PublishesPartialAndBlocksBothProposalSurfaces()
    {
        await using var container = new RedisBuilder("redis:7.2-alpine").Build();
        await container.StartAsync();
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var store = new RedisWorkflowOperationStore(multiplexer, NullLogger<RedisWorkflowOperationStore>.Instance);
        Assert.True(await store.TryCreateAsync(
            WorkflowSourceFixture.SucceededDeploy(WorkflowSourceFixture.TargetA, WorkflowSourceFixture.PriorArtifact)));
        var ledger = new OpsFindingsCollectionLedger();
        var clock = new SettableClock(WorkflowSourceFixture.T0);
        var options = WorkflowSourceFixture.DeclaredReleaseOptions();

        // Keys stay readable for the active set, the terminal index, the seeded operation and target A's
        // succeeded-deploy index; the server refuses target B's index, so the pass answers for A and not for B.
        var denied = await container.ExecAsync(
        [
            "redis-cli", "ACL", "SETUSER", "default", "resetkeys",
            "~controlplane:workflow:active*",
            "~controlplane:workflow:terminal",
            "~controlplane:workflow:op-4840-*",
            $"~controlplane:workflow:deploy-succeeded:{WorkflowSourceFixture.TargetA}",
        ]);
        Assert.Equal("OK", denied.Stdout.Trim());
        var gateway = WorkflowSourceFixture.CreateGateway();
        var service = WorkflowSourceFixture.CreateService(store, options, ledger, clock, gateway);

        var evaluation = await service.EvaluateWithEvidenceAsync();

        WorkflowSourceFixture.AssertPartialTargetCoverage(WorkflowSourceFixture.WorkflowSource(evaluation), WorkflowSourceFixture.T0);
        var finding = WorkflowSourceFixture.DivergenceFinding(evaluation, WorkflowSourceFixture.TargetA);
        Assert.DoesNotContain(evaluation.Findings, f => f.Subject.TargetId == WorkflowSourceFixture.TargetB);
        var proposed = await service.ProposeAsync(finding.Id);
        Assert.Equal(OpsFindingProposalStatus.Blocked, proposed.Status);
        Assert.Equal("evidencePostureNotActionable", proposed.Message);
        using (var services = McpPlatformOpsReaderTests.CreateServices(gateway, findings: service))
        {
            var mcp = await McpPlatformOpsReaderTests.CreateReader(services: services).ProposeFindingAsync(
                McpPlatformOpsReaderTests.CreatePrincipal(),
                new McpProposeFindingArgument { FindingId = finding.Id, CandidateId = WorkflowSourceFixture.TargetA },
                CancellationToken.None);
            Assert.Equal("Blocked", mcp.Outcome);
            Assert.Equal("evidencePostureNotActionable", mcp.Message);
            await services.GetRequiredService<IOperationEnvelopeFactory>().DidNotReceive().CreateAcceptedAsync(
                Arg.Any<string>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>());
        }

        Assert.Empty(gateway.ReceivedCalls());

        // Restoring key access is the only change, and the same finding becomes proposable again.
        var restored = await container.ExecAsync(["redis-cli", "ACL", "SETUSER", "default", "resetkeys", "~*"]);
        Assert.Equal("OK", restored.Stdout.Trim());
        var restoredAt = WorkflowSourceFixture.T0.AddMinutes(1);
        clock.Now = restoredAt;
        var restoredId = await WorkflowSourceFixture.AssertCompleteAndProposableAsync(store, options, ledger, clock, restoredAt);
        Assert.Equal(finding.Id, restoredId);
    }

    /// <summary>
    /// #4938: a deploy parked in ManualInterventionRequired through the production Redis store yields
    /// the manual-intervention finding with its rollback action, although the store keeps that status
    /// out of the active index. The finding clears when the operation is rolled back, or when a later
    /// deploy of the same target succeeds.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task DeployManualIntervention_RedisStore_FiresUntilRolledBackOrSuperseded()
    {
        await using var container = new RedisBuilder("redis:7.2-alpine").Build();
        await container.StartAsync();
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var store = new RedisWorkflowOperationStore(multiplexer, NullLogger<RedisWorkflowOperationStore>.Instance);
        var stuckAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var stuckA = WorkflowSourceFixture.StuckDeploy(WorkflowSourceFixture.TargetA, stuckAt);
        var stuckB = WorkflowSourceFixture.StuckDeploy(WorkflowSourceFixture.TargetB, stuckAt);
        Assert.True(await store.TryCreateAsync(stuckA));
        Assert.True(await store.TryCreateAsync(stuckB));
        // The store keeps the status out of the active index, so reconcilers do not re-drive it.
        Assert.Empty(await store.ListActiveAsync(WorkflowOperationKind.Deploy));
        var ledger = new OpsFindingsCollectionLedger();
        var clock = new SettableClock(WorkflowSourceFixture.T0);

        var gateway = WorkflowSourceFixture.CreateGateway();
        var service = WorkflowSourceFixture.CreateService(store, new ControlPlaneOptions(), ledger, clock, gateway);
        var evaluation = await service.EvaluateWithEvidenceAsync();

        Assert.Equal(
            EvidencePostureVocabulary.Completeness.Complete,
            WorkflowSourceFixture.WorkflowSource(evaluation).Completeness);
        var findings = evaluation.Findings
            .Where(f => f.Rule == OpsFindingsService.RuleDeployManualIntervention)
            .OrderBy(f => f.Subject.TargetId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal([stuckA.OperationId, stuckB.OperationId], findings.Select(f => f.Subject.OperationId));
        var findingA = findings[0];
        Assert.Equal(OpsFindingSeverity.Critical, findingA.Severity);
        Assert.Equal(OperationClass.Deploy, findingA.RecommendedAction?.Kind);
        Assert.Contains(
            $"\"desiredRevision\":\"{WorkflowSourceFixture.PriorArtifact}\"",
            findingA.RecommendedAction!.ExecutionPayload,
            StringComparison.Ordinal);
        Assert.Equal(OpsFindingProposalStatus.ProposalCreated, (await service.ProposeAsync(findingA.Id)).Status);

        // B is rolled back; a later deploy of A (the proposed rollback) succeeds.
        var rolledBackB = (await store.GetAsync(stuckB.OperationId))! with
        {
            Status = WorkflowOperationStatus.RolledBack,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await store.SetAsync(rolledBackB);
        Assert.True(await store.TryCreateAsync(
            WorkflowSourceFixture.SucceededDeploy(WorkflowSourceFixture.TargetA, WorkflowSourceFixture.PriorArtifact)));
        clock.Now = WorkflowSourceFixture.T0.AddMinutes(1);

        var resolved = await WorkflowSourceFixture
            .CreateService(store, new ControlPlaneOptions(), ledger, clock, WorkflowSourceFixture.CreateGateway())
            .EvaluateWithEvidenceAsync();

        Assert.Equal(
            EvidencePostureVocabulary.Completeness.Complete,
            WorkflowSourceFixture.WorkflowSource(resolved).Completeness);
        Assert.DoesNotContain(resolved.Findings, f => f.Rule == OpsFindingsService.RuleDeployManualIntervention);
    }

    private static ConfigurationOptions OutageTolerant(string connectionString)
    {
        var configuration = ConfigurationOptions.Parse(connectionString);
        configuration.AbortOnConnectFail = false;
        // The outage phase fails fast through the backlog policy once the loss is observed, so the
        // timeouts only need to bound the healthy phases, and those run on busy shared runners (#5002).
        configuration.AsyncTimeout = 10_000;
        configuration.ConnectTimeout = 10_000;
        configuration.BacklogPolicy = BacklogPolicy.FailFast;
        configuration.ReconnectRetryPolicy = new ExponentialRetry(100);
        return configuration;
    }

    private static Task ObserveInteractiveConnectionFailure(ConnectionMultiplexer multiplexer)
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        multiplexer.ConnectionFailed += (_, args) =>
        {
            if (args.ConnectionType == ConnectionType.Interactive)
            {
                failed.TrySetResult();
            }
        };
        return failed.Task;
    }

    private static async Task WaitForRedisAsync(ConnectionMultiplexer multiplexer)
    {
        using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                await multiplexer.GetDatabase().PingAsync();
                return;
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                await Task.Delay(100, recoveryTimeout.Token);
            }
        }
    }
}

internal static class WorkflowSourceFixture
{
    public const string TargetA = "serving-a";

    public const string TargetB = "serving-b";

    public const string PriorArtifact = "ghcr.io/honua/server:2026.0.9";

    public static readonly DateTimeOffset T0 = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan SignalValidity = TimeSpan.FromMinutes(5);

    public static WorkflowOperationRecord SucceededDeploy(string targetId, string revision)
    {
        var at = DateTimeOffset.UtcNow;
        return new WorkflowOperationRecord
        {
            OperationId = $"op-4840-{targetId}-succeeded",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Succeeded,
            CreatedAt = at,
            UpdatedAt = at,
            CompletedAt = at,
            Deploy = new DeployOperationSpec
            {
                TargetId = targetId,
                TargetKind = DeployTargetKind.SelfHostedRolling,
                Backend = "self-hosted",
                Environment = "prod",
                TargetName = targetId,
                DesiredRevision = revision,
            },
        };
    }

    public static WorkflowOperationRecord StuckDeploy(string targetId, DateTimeOffset createdAt)
        => new()
        {
            OperationId = $"op-4938-{targetId}-stuck",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.ManualInterventionRequired,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            CompletedAt = createdAt,
            Deploy = new DeployOperationSpec
            {
                TargetId = targetId,
                TargetKind = DeployTargetKind.SelfHostedRolling,
                Backend = "self-hosted",
                Environment = "prod",
                TargetName = targetId,
                CurrentRevision = PriorArtifact,
                DesiredRevision = "ghcr.io/honua/server:2026.1.1",
            },
        };

    public static ControlPlaneOptions DeclaredReleaseOptions(params string[] targetIds)
        => new()
        {
            PlatformRelease = new PlatformReleaseOptions
            {
                Version = "2026.1.1",
                ServingArtifactReference = "ghcr.io/honua/server:2026.1.1",
            },
            DeployTargets = (targetIds.Length == 0 ? [TargetA, TargetB] : targetIds)
                .Select(targetId => new DeployTargetOptions
                {
                    TargetId = targetId,
                    TargetKind = DeployTargetKind.SelfHostedRolling,
                    Backend = "self-hosted",
                    Environment = "prod",
                    TargetName = targetId,
                })
                .ToList(),
        };

    public static IOperationGateway CreateGateway()
    {
        var gateway = Substitute.For<IOperationGateway>();
        var result = new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.ProposalCreated,
            Decision = new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, HonuaEdition.Pro, "test"),
            ProposalId = "proposal-4840",
        };
        gateway.RouteAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>()).Returns(result);
        gateway.CreateApprovalProposalAsync(Arg.Any<string>(), Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>())
            .Returns(result);
        return gateway;
    }

    public static OpsFindingsService CreateService(
        IWorkflowOperationStore store,
        ControlPlaneOptions controlPlane,
        OpsFindingsCollectionLedger ledger,
        TimeProvider clock,
        IOperationGateway? gateway = null)
    {
        var probe = Substitute.For<IDeployPreflightProbe>();
        probe.ProbeAsync(Arg.Any<CancellationToken>()).Returns(new DeployPreflightSnapshot
        {
            Status = "ready",
            ReadyForCoordinatedDeploy = true,
            Message = "ready",
            Readiness = new DeployPreflightReadinessSnapshot { IsReady = true, StatusCode = 200, Message = "ok" },
            Migration = new DeployPreflightMigrationSnapshot { LifecycleStatus = "succeeded", PlanAvailable = true },
        });
        return new OpsFindingsService(
            new StaticOptionsMonitor<OpsFindingsOptions>(new OpsFindingsOptions()),
            new StaticOptionsMonitor<ControlPlaneOptions>(controlPlane),
            Substitute.For<IAlertDispatchHealth>(),
            probe,
            gateway: gateway ?? CreateGateway(),
            workflowStore: store,
            extendedSignals: new OpsFindingsExtendedSignals { CollectionLedger = ledger, TimeProvider = clock });
    }

    public static EvidenceSourceEnvelope WorkflowSource(OpsFindingsEvaluation evaluation)
        => evaluation.Posture.Sources.Single(source =>
            source.SourceId == EvidencePostureVocabulary.SourceIds.FindingsWorkflowOperations);

    public static OpsFinding DivergenceFinding(OpsFindingsEvaluation evaluation, string targetId)
    {
        var finding = evaluation.Findings.Single(f =>
            f.Rule == OpsFindingsService.RulePlatformReleaseRuntimeDivergence && f.Subject.TargetId == targetId);
        Assert.Equal(OperationClass.Deploy, finding.RecommendedAction?.Kind);
        return finding;
    }

    public static void AssertPartialTargetCoverage(EvidenceSourceEnvelope source, DateTimeOffset collectedAt)
    {
        Assert.Equal(EvidencePostureVocabulary.Completeness.Partial, source.Completeness);
        Assert.Equal(EvidencePostureVocabulary.BackendKinds.DurableStore, source.BackendKind);
        Assert.Equal(collectedAt, source.ObservedAt);
        Assert.Equal(collectedAt, source.LastSuccessfulAt);
        Assert.Equal(
            [EvidencePostureVocabulary.ReasonCodes.IncompleteCoverage, EvidencePostureVocabulary.ReasonCodes.PartialResult],
            source.ReasonCodes);
        Assert.NotNull(source.Coverage);
        Assert.Equal(
            ["active-deploy-operations", $"deploy-target:{TargetA}", "manual-intervention-deploy-operations"],
            source.Coverage!.IncludedComponentIds);
        Assert.Equal(
            ["active-deploy-operations", $"deploy-target:{TargetA}", $"deploy-target:{TargetB}", "manual-intervention-deploy-operations"],
            source.Coverage.ExpectedComponentIds);
    }

    /// <summary>
    /// Asserts a complete, correctly clocked source and that target A's divergence finding reaches the
    /// gateway exactly once through each proposal surface; returns that finding's id.
    /// </summary>
    public static async Task<string> AssertCompleteAndProposableAsync(
        IWorkflowOperationStore store,
        ControlPlaneOptions options,
        OpsFindingsCollectionLedger ledger,
        TimeProvider clock,
        DateTimeOffset collectedAt)
    {
        var gateway = CreateGateway();
        var service = CreateService(store, options, ledger, clock, gateway);

        var evaluation = await service.EvaluateWithEvidenceAsync();

        var source = WorkflowSource(evaluation);
        Assert.Equal(EvidencePostureVocabulary.Completeness.Complete, source.Completeness);
        Assert.Equal(EvidencePostureVocabulary.BackendKinds.DurableStore, source.BackendKind);
        Assert.Equal("workflow-operation-store", source.BackendId);
        Assert.Equal(collectedAt, source.ObservedAt);
        Assert.Equal(collectedAt, source.LastSuccessfulAt);
        Assert.Equal(collectedAt.Add(SignalValidity), source.ValidUntil);
        Assert.Empty(source.ReasonCodes);
        var finding = DivergenceFinding(evaluation, TargetA);

        Assert.Equal(OpsFindingProposalStatus.ProposalCreated, (await service.ProposeAsync(finding.Id)).Status);
        await gateway.Received(1).RouteAsync(Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>());
        using var services = McpPlatformOpsReaderTests.CreateServices(gateway, findings: service);
        var mcp = await McpPlatformOpsReaderTests.CreateReader(services: services).ProposeFindingAsync(
            McpPlatformOpsReaderTests.CreatePrincipal(),
            new McpProposeFindingArgument { FindingId = finding.Id, CandidateId = TargetA },
            CancellationToken.None);
        Assert.Equal(nameof(OperationGatewayOutcome.ProposalCreated), mcp.Outcome);
        await gateway.Received(1).CreateApprovalProposalAsync(
            Arg.Any<string>(), Arg.Any<OperationGatewayRequest>(), Arg.Any<CancellationToken>());
        return finding.Id;
    }
}

internal sealed class SettableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
