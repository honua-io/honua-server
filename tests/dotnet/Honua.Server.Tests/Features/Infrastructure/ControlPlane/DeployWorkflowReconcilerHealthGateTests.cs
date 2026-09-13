// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// End-to-end health-gated rollback coverage (#1849 / #2163). Drives the REAL
/// <see cref="DeployTelemetrySignalEvaluator"/> with a synthetic <c>/healthz/ready</c> probe through
/// the <see cref="DeployWorkflowReconciler"/> against an arbitrary deploy backend, proving the
/// health gate is inherited by the backend rollout path (no hard-flip): an unhealthy probe drives the
/// SAME automatic-rollback path as an error-rate/latency breach, and a healthy probe promotes.
/// </summary>
public sealed class DeployWorkflowReconcilerHealthGateTests
{
    [Fact]
    public async Task Reconcile_HealthProbeUnhealthy_DrivesBackendRollback()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Succeeded);
        var operation = CreateOperation();
        await store.TryCreateAsync(operation);

        var evaluator = CreateRealEvaluator(
            new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 3 }));
        var reconciler = CreateReconciler(store, backend, evaluator);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated.Should().NotBeNull();
        backend.RollbackCalls.Should().Be(1, "an unhealthy synthetic health probe must trigger the backend rollback");
        updated!.Status.Should().BeOneOf(
            WorkflowOperationStatus.RollbackRequested,
            WorkflowOperationStatus.RolledBack);
        updated.CurrentPhase.Should().Contain("health probe is unhealthy");
    }

    [Fact]
    public async Task Reconcile_HealthProbeHealthy_DoesNotRollBack()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Succeeded);
        var operation = CreateOperation();
        await store.TryCreateAsync(operation);

        var evaluator = CreateRealEvaluator(
            new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 0 }));
        var reconciler = CreateReconciler(store, backend, evaluator);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated.Should().NotBeNull();
        backend.RollbackCalls.Should().Be(0, "a healthy synthetic health probe must not trigger a rollback");
        updated!.Status.Should().NotBe(WorkflowOperationStatus.RollbackRequested);
    }

    [Fact]
    public async Task Reconcile_GoldenQueryCorrupt_BlocksAutoPromotionAndDrivesRollback()
    {
        // Healthy-but-corrupt (#2811): the synthetic health probe is healthy AND the backend recommends
        // promotion (status 200, 5xx/p95 nominal), but the golden-query correctness gate detects a
        // wrong/garbled response body. The corrupt release must NOT be promoted; it must roll back.
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(
            observeStatus: WorkflowOperationStatus.Succeeded,
            promotionRecommended: true);
        var operation = CreateOperation(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["telemetry.golden_query.url"] = "https://example.com/rest/services/probe",
            ["telemetry.golden_query.expected_contains"] = "GOLDEN-OK"
        });
        await store.TryCreateAsync(operation);

        var evaluator = CreateRealEvaluator(new FakeHealthProbe(
            new DeployHealthProbeResult { Attempts = 3, Failures = 0 },
            goldenResult: new DeployGoldenQueryResult { Matched = false, Detail = "response body did not contain the required golden token" }));
        var reconciler = CreateReconciler(store, backend, evaluator);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated.Should().NotBeNull();
        backend.PromoteCalls.Should().Be(0, "a release that fails the golden-query correctness gate must not auto-promote");
        backend.RollbackCalls.Should().Be(1, "a corrupt-but-healthy release must roll back via the golden-query gate");
        updated!.Status.Should().BeOneOf(
            WorkflowOperationStatus.RollbackRequested,
            WorkflowOperationStatus.RolledBack);
        updated.CurrentPhase.Should().Contain("golden-query correctness gate");
    }

    [Fact]
    public async Task Reconcile_GoldenQueryMatches_DoesNotBlockOnCorrectness()
    {
        // The correctness gate is not a hard brake: when the golden-query body matches, the release is
        // not rolled back on correctness grounds (it falls through to the rest of the gate).
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Succeeded);
        var operation = CreateOperation(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["telemetry.golden_query.url"] = "https://example.com/rest/services/probe",
            ["telemetry.golden_query.expected_contains"] = "GOLDEN-OK"
        });
        await store.TryCreateAsync(operation);

        var evaluator = CreateRealEvaluator(new FakeHealthProbe(
            new DeployHealthProbeResult { Attempts = 3, Failures = 0 },
            goldenResult: new DeployGoldenQueryResult { Matched = true }));
        var reconciler = CreateReconciler(store, backend, evaluator);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated.Should().NotBeNull();
        backend.RollbackCalls.Should().Be(0, "a release that passes the golden-query gate must not roll back on correctness grounds");
        updated!.Status.Should().NotBe(WorkflowOperationStatus.RollbackRequested);
    }

    // ---- traffic exposure, bounded evidence and controller restart (#4617) ----

    private static readonly IReadOnlyDictionary<string, string> HealthOnlyParameters =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["telemetry.policy"] = "health-only",
            ["telemetry.healthz.url"] = "https://example.com/healthz/ready"
        };

    [Fact]
    public async Task Reconcile_CandidateNotYetExposed_HoldsWithoutStampingExposureOrActivating()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Submitted, promotionRecommended: true);
        var operation = CreateOperationWith(HealthOnlyParameters, DateTimeOffset.UtcNow.AddMinutes(-10), WorkflowOperationStatus.Submitted);
        await store.TryCreateAsync(operation);
        var reconciler = CreateReconciler(
            store,
            backend,
            CreateRealEvaluator(new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 0 })));

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated!.Deploy!.TrafficExposedAt.Should().BeNull("the backend has not reported the candidate serving traffic");
        updated.CurrentPhase.Should().Contain("receive traffic");
        backend.PromoteCalls.Should().Be(0);
        backend.RollbackCalls.Should().Be(0);
    }

    [Fact]
    public async Task Reconcile_DelayedExposure_AnchorsWarmupOnPersistedExposure_AcrossControllerRestart()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Submitted, promotionRecommended: true);
        // Created 20 minutes ago — inside the 30-minute exposure deadline, but long enough that a
        // CreatedAt-anchored 2-minute warmup would already have elapsed.
        var operation = CreateOperationWith(HealthOnlyParameters, DateTimeOffset.UtcNow.AddMinutes(-20), WorkflowOperationStatus.Submitted);
        await store.TryCreateAsync(operation);
        var healthy = new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 0 });

        var firstController = CreateReconciler(store, backend, CreateRealEvaluator(healthy));
        await firstController.ReconcileWorkflowOperationAsync(operation.OperationId);
        var held = await store.GetAsync(operation.OperationId);
        held!.Deploy!.TrafficExposedAt.Should().BeNull();
        held.Status.Should().Be(WorkflowOperationStatus.Submitted, "a pre-exposure hold must not look like a serving candidate");

        // The backend now reports the candidate serving traffic.
        backend.ObserveStatus = WorkflowOperationStatus.Reconciling;
        var beforeExposure = DateTimeOffset.UtcNow;
        await firstController.ReconcileWorkflowOperationAsync(operation.OperationId);
        var exposed = await store.GetAsync(operation.OperationId);
        var exposedAt = exposed!.Deploy!.TrafficExposedAt;
        exposedAt.Should().NotBeNull();
        exposedAt!.Value.Should().BeOnOrAfter(beforeExposure);
        exposed.CurrentPhase.Should().Contain("warmup", "warmup starts at exposure, not at the hour-old creation time");
        backend.PromoteCalls.Should().Be(0);

        // Controller restart: a fresh reconciler and evaluator resume from the persisted record.
        var restartedController = CreateReconciler(store, backend, CreateRealEvaluator(healthy));
        await restartedController.ReconcileWorkflowOperationAsync(operation.OperationId);
        var resumed = await store.GetAsync(operation.OperationId);
        resumed!.Deploy!.TrafficExposedAt.Should().Be(exposedAt, "the exposure stamp is persisted and never re-stamped");
        resumed.CurrentPhase.Should().Contain("warmup");
        backend.PromoteCalls.Should().Be(0);

        // Once the persisted exposure is older than warmup, the restarted controller promotes on it —
        // through the explicit health-only profile, with no metrics connection configured at all.
        await store.SetAsync(resumed with { Deploy = resumed.Deploy with { TrafficExposedAt = DateTimeOffset.UtcNow.AddMinutes(-3) } });
        await restartedController.ReconcileWorkflowOperationAsync(operation.OperationId);
        backend.PromoteCalls.Should().Be(1);
        backend.RollbackCalls.Should().Be(0);
    }

    [Fact]
    public async Task Reconcile_NeverExposedPastDeadline_FailsWithoutActivation()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Submitted, promotionRecommended: true);
        var operation = CreateOperationWith(HealthOnlyParameters, DateTimeOffset.UtcNow.AddHours(-2), WorkflowOperationStatus.Submitted);
        await store.TryCreateAsync(operation);
        var reconciler = CreateReconciler(
            store,
            backend,
            CreateRealEvaluator(new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 0 })));

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        backend.PromoteCalls.Should().Be(0, "a candidate that never received traffic must never be activated");
        backend.RollbackCalls.Should().Be(1, "the stalled rollout is aborted through the backend rather than left waiting");
        updated!.Status.Should().BeOneOf(WorkflowOperationStatus.RollbackRequested, WorkflowOperationStatus.RolledBack);
        updated.CurrentPhase.Should().Contain("never observed receiving traffic");
        updated.Deploy!.TrafficExposedAt.Should().BeNull();
    }

    [Fact]
    public async Task Reconcile_MetricsProviderOutage_HoldsWithinGrace_ThenRollsBackWithoutEverPromoting()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Reconciling, promotionRecommended: true);
        var operation = CreateOperationWith(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod",
                ["telemetry.evidence_grace_seconds"] = "120"
            },
            DateTimeOffset.UtcNow.AddHours(-1),
            WorkflowOperationStatus.Reconciling,
            trafficExposedAt: DateTimeOffset.UtcNow.AddMinutes(-3));
        await store.TryCreateAsync(operation);
        var reconciler = CreateReconciler(store, backend, CreateMetricsEvaluator(HttpStatusCode.ServiceUnavailable));

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var holding = await store.GetAsync(operation.OperationId);

        holding!.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        holding.CurrentPhase.Should().Contain("unavailable");
        backend.PromoteCalls.Should().Be(0, "the backend's promotion recommendation cannot pass an unverifiable metric gate");
        backend.RollbackCalls.Should().Be(0, "inside the grace window an outage only holds");

        // The outage outlasts warmup + grace.
        await store.SetAsync(holding with { Deploy = holding.Deploy! with { TrafficExposedAt = DateTimeOffset.UtcNow.AddMinutes(-5) } });
        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var escalated = await store.GetAsync(operation.OperationId);

        backend.PromoteCalls.Should().Be(0);
        backend.RollbackCalls.Should().Be(1);
        escalated!.Status.Should().BeOneOf(WorkflowOperationStatus.RollbackRequested, WorkflowOperationStatus.RolledBack);
        escalated.CurrentPhase.Should().Contain("evidence remained unavailable");
    }

    [Fact]
    public async Task Reconcile_HealthPromotionGateWithMetricsPolicy_DoesNotDegradeToHealthOnly()
    {
        // The operator chose the health promotion gate AND a metrics policy whose connection is not
        // configured. The backend's own health recommendation must not promote past the unverifiable
        // metric gate; the missing evidence holds, then escalates.
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Reconciling, promotionRecommended: true);
        var operation = CreateOperationWith(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DeployPromotionPolicy.PromotionGateParameterKey] = "health",
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.prometheus.job"] = "honua-prod",
                ["telemetry.healthz.url"] = "https://example.com/healthz/ready",
                ["telemetry.evidence_grace_seconds"] = "60"
            },
            DateTimeOffset.UtcNow.AddHours(-1),
            WorkflowOperationStatus.Reconciling,
            trafficExposedAt: DateTimeOffset.UtcNow.AddMinutes(-2.5));
        await store.TryCreateAsync(operation);
        var reconciler = CreateReconciler(
            store,
            backend,
            CreateRealEvaluator(new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 0 })));

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var holding = await store.GetAsync(operation.OperationId);

        backend.PromoteCalls.Should().Be(0);
        holding!.CurrentPhase.Should().Contain("not configured");

        await store.SetAsync(holding with { Deploy = holding.Deploy! with { TrafficExposedAt = DateTimeOffset.UtcNow.AddMinutes(-10) } });
        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);

        backend.PromoteCalls.Should().Be(0);
        backend.RollbackCalls.Should().Be(1);
    }

    [Fact]
    public async Task Reconcile_GoldenQueryReturnsHttp200ErrorEnvelope_BlocksPromotionAndRollsBack()
    {
        // Real HTTP probe: readiness answers 200 "Healthy", but the GIS golden query answers 200 with a
        // GeoServices error envelope. Status-only checks would promote this release.
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Reconciling, promotionRecommended: true);
        var parameters = new Dictionary<string, string>(HealthOnlyParameters, StringComparer.Ordinal)
        {
            ["telemetry.golden_query.url"] = "https://example.com/rest/services/parcels/FeatureServer/0/query",
            ["telemetry.golden_query.expected_contains"] = "\"features\""
        };
        var operation = CreateOperationWith(
            parameters,
            DateTimeOffset.UtcNow.AddHours(-1),
            WorkflowOperationStatus.Reconciling,
            trafficExposedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        await store.TryCreateAsync(operation);
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath.StartsWith("/healthz", StringComparison.Ordinal)
            ? Respond(HttpStatusCode.OK, "Healthy")
            : Respond(HttpStatusCode.OK, "{\"error\":{\"code\":400,\"message\":\"Unable to complete operation.\",\"details\":[\"\\\"features\\\" query failed\"]}}"));
        var probe = new HttpDeployHealthProbe(new StubHttpClientFactory(new Honua.TestKit.CallerOwnedHttpClient(handler)));
        var reconciler = CreateReconciler(store, backend, CreateRealEvaluator(probe));

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        backend.PromoteCalls.Should().Be(0, "an HTTP-200 error envelope is a wrong result, not a healthy release");
        backend.RollbackCalls.Should().Be(1);
        updated!.CurrentPhase.Should().Contain("error envelope");
    }

    // ---- backends that stage the candidate without traffic (#4617) ----------

    private static readonly IReadOnlyDictionary<string, string> StagedMetricsParameters =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.prometheus.job"] = "honua-prod",
            ["telemetry.healthz.url"] = "https://example.com/healthz/ready",
            ["telemetry.evidence_grace_seconds"] = "600"
        };

    [Fact]
    public async Task Reconcile_StagedCandidateWithMetricsPolicy_CutsOverOnPreCutoverChecks_AndStampsExposureAtCutover()
    {
        // A single-switch backend reports Reconciling while its standby serves no traffic. Before #4617's
        // follow-up the reconciler stamped that as exposure and demanded metrics the candidate could never
        // produce, so the rollout held on warmup and a sample floor and rolled back without ever cutting over.
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Reconciling, promotionRecommended: true)
        {
            StagesCandidateWithoutTraffic = true
        };
        var operation = CreateOperationWith(StagedMetricsParameters, DateTimeOffset.UtcNow.AddMinutes(-1), WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);
        var queries = new ConcurrentQueue<string>();
        var reconciler = CreateReconciler(
            store,
            backend,
            CreateMetricsEvaluator(queries, _ => Respond(HttpStatusCode.OK, PrometheusEmptyVector), HealthyProbe()));

        var beforeCutover = DateTimeOffset.UtcNow;
        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        backend.PromoteCalls.Should().Be(1, "the staged candidate passed its backend health gate and readiness probe");
        backend.RollbackCalls.Should().Be(0);
        queries.Should().BeEmpty("no candidate traffic, and so no candidate metric, exists before cutover");
        updated!.Status.Should().Be(WorkflowOperationStatus.Reconciling, "the promoted candidate is observed before the deploy commits");
        updated.Deploy!.Protection!.Phase.Should().Be(DeployProtectionPhase.Observing);
        updated.Deploy.TrafficExposedAt.Should().NotBeNull();
        updated.Deploy.TrafficExposedAt!.Value.Should().BeOnOrAfter(beforeCutover, "exposure starts at the cutover, not at standby launch");
    }

    [Fact]
    public async Task Reconcile_StagedCandidateReadinessUnhealthy_RollsBackWithoutCutover()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Reconciling, promotionRecommended: true)
        {
            StagesCandidateWithoutTraffic = true
        };
        var operation = CreateOperationWith(StagedMetricsParameters, DateTimeOffset.UtcNow.AddMinutes(-1), WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);
        var queries = new ConcurrentQueue<string>();
        var reconciler = CreateReconciler(
            store,
            backend,
            CreateMetricsEvaluator(
                queries,
                _ => Respond(HttpStatusCode.OK, PrometheusEmptyVector),
                new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 3 })));

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        backend.PromoteCalls.Should().Be(0);
        backend.RollbackCalls.Should().Be(1);
        updated!.Deploy!.TrafficExposedAt.Should().BeNull("the candidate never received traffic");
        updated.CurrentPhase.Should().Contain("synthetic health probe is unhealthy");
        queries.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_StagedCandidateNeverHealthy_RollsBackAtExposureDeadlineWithoutCutover()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Reconciling, promotionRecommended: false)
        {
            StagesCandidateWithoutTraffic = true
        };
        var operation = CreateOperationWith(StagedMetricsParameters, DateTimeOffset.UtcNow.AddMinutes(-31), WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);
        var reconciler = CreateReconciler(
            store,
            backend,
            CreateMetricsEvaluator(new ConcurrentQueue<string>(), _ => Respond(HttpStatusCode.OK, PrometheusEmptyVector), HealthyProbe()));

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        backend.PromoteCalls.Should().Be(0);
        backend.RollbackCalls.Should().Be(1);
        updated!.Deploy!.TrafficExposedAt.Should().BeNull();
        updated.CurrentPhase.Should().Contain("never passed the backend health gate within the 1800-second exposure deadline");
    }

    [Fact]
    public async Task Reconcile_StagedCandidateReadyOnlyAfterExposureDeadline_RollsBackWithoutCutover()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Reconciling, promotionRecommended: true)
        {
            StagesCandidateWithoutTraffic = true
        };
        var operation = CreateOperationWith(StagedMetricsParameters, DateTimeOffset.UtcNow.AddMinutes(-31), WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);
        var reconciler = CreateReconciler(
            store,
            backend,
            CreateMetricsEvaluator(new ConcurrentQueue<string>(), _ => Respond(HttpStatusCode.OK, PrometheusEmptyVector), HealthyProbe()));

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        backend.PromoteCalls.Should().Be(0, "a standby that turns ready after the exposure deadline is never cut over late");
        backend.RollbackCalls.Should().Be(1);
        updated!.Deploy!.TrafficExposedAt.Should().BeNull();
        updated.CurrentPhase.Should().Contain("was not ready for cutover within the 1800-second exposure deadline");
    }

    [Fact]
    public async Task Reconcile_ObservationWindowElapsedWhileMetricsEvidenceMissing_StaysUncommitted_ThenRollsBack()
    {
        // Promoted five minutes ago, window deadline already past, but Prometheus has no candidate samples.
        // Committing here would be a missing-data success path.
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Reconciling);
        var exposedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var operation = CreateOperationWith(
            StagedMetricsParameters,
            DateTimeOffset.UtcNow.AddHours(-1),
            WorkflowOperationStatus.Reconciling,
            trafficExposedAt: exposedAt,
            protection: CreateObservingProtection(exposedAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await store.TryCreateAsync(operation);
        var reconciler = CreateReconciler(
            store,
            backend,
            CreateMetricsEvaluator(new ConcurrentQueue<string>(), _ => Respond(HttpStatusCode.OK, PrometheusEmptyVector), HealthyProbe()));

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var holding = await store.GetAsync(operation.OperationId);

        backend.CompleteProtectionCalls.Should().Be(0, "the window must not commit while the telemetry gate is waiting for evidence");
        backend.RollbackCalls.Should().Be(0, "missing evidence is still inside warmup plus the evidence grace");
        holding!.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        holding.CompletedAt.Should().BeNull();
        holding.Deploy!.Protection!.Phase.Should().Be(DeployProtectionPhase.Observing);
        holding.Deploy.Protection.ReasonCode.Should().Be(DeployWorkflowReconciler.TelemetryEvidencePendingReasonCode);
        holding.CurrentPhase.Should().Contain("telemetry gate has not passed");

        // Evidence stays absent past warmup plus the grace window: the bounded recovery policy rolls back.
        await store.SetAsync(holding with { Deploy = holding.Deploy with { TrafficExposedAt = DateTimeOffset.UtcNow.AddMinutes(-30) } });
        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var recovered = await store.GetAsync(operation.OperationId);

        backend.CompleteProtectionCalls.Should().Be(0);
        backend.RollbackCalls.Should().Be(1);
        recovered!.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        recovered.CurrentPhase.Should().Contain("evidence remained unavailable");
    }

    [Fact]
    public async Task Reconcile_ObservationWindowElapsedOnceMetricsEvidencePasses_Commits()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Reconciling);
        var exposedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var operation = CreateOperationWith(
            StagedMetricsParameters,
            DateTimeOffset.UtcNow.AddHours(-1),
            WorkflowOperationStatus.Reconciling,
            trafficExposedAt: exposedAt,
            protection: CreateObservingProtection(exposedAt, DateTimeOffset.UtcNow.AddSeconds(-1)) with
            {
                ReasonCode = DeployWorkflowReconciler.TelemetryEvidencePendingReasonCode
            });
        await store.TryCreateAsync(operation);
        var queries = new ConcurrentQueue<string>();
        var reconciler = CreateReconciler(
            store,
            backend,
            CreateMetricsEvaluator(queries, HealthyPrometheusAnswer, HealthyProbe()));

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        queries.Should().HaveCount(3, "the sample floor, error rate and latency are all read from the candidate's traffic");
        backend.RollbackCalls.Should().Be(0);
        backend.CompleteProtectionCalls.Should().Be(1);
        updated!.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        updated.Deploy!.Protection!.Phase.Should().Be(DeployProtectionPhase.Expired);
    }

    // ---- helpers ---------------------------------------------------------

    private static DeployWorkflowReconciler CreateReconciler(
        IWorkflowOperationStore store,
        IDeployBackend backend,
        IDeployTelemetrySignalEvaluator evaluator)
        => new(
            store,
            new SingleTargetRegistry(),
            [backend],
            evaluator,
            NullLogger<DeployWorkflowReconciler>.Instance);

    private static DeployTelemetrySignalEvaluator CreateRealEvaluator(IDeployHealthProbe probe)
        => new(
            new OptionsMonitorStub(new ControlPlaneOptions()),
            [],
            NullLogger<DeployTelemetrySignalEvaluator>.Instance,
            probe);

    private static DeployTelemetrySignalEvaluator CreateMetricsEvaluator(HttpStatusCode prometheusStatus)
        => CreateMetricsEvaluator(
            new ConcurrentQueue<string>(),
            _ => Respond(prometheusStatus, "{\"status\":\"error\",\"error\":\"upstream unavailable\"}"));

    private static DeployTelemetrySignalEvaluator CreateMetricsEvaluator(
        ConcurrentQueue<string> queries,
        Func<string, HttpResponseMessage> answer,
        IDeployHealthProbe? probe = null)
    {
        var handler = new RoutingHandler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri?.Query ?? string.Empty);
            queries.Enqueue(query);
            return answer(query);
        });
        var provider = new PrometheusDeployTelemetryProviderEvaluator(
            new StubHttpClientFactory(new Honua.TestKit.CallerOwnedHttpClient(handler)));

        return new DeployTelemetrySignalEvaluator(
            new OptionsMonitorStub(new ControlPlaneOptions
            {
                TelemetryConnections =
                [
                    new DeployTelemetryConnectionOptions
                    {
                        ConnectionId = "prod-prom",
                        Provider = "prometheus",
                        BaseUrl = "https://example.com",
                        TimeoutSeconds = 2
                    }
                ]
            }),
            [provider],
            NullLogger<DeployTelemetrySignalEvaluator>.Instance,
            probe);
    }

    private const string PrometheusEmptyVector = "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[]}}";

    private static FakeHealthProbe HealthyProbe()
        => new(new DeployHealthProbeResult { Attempts = 3, Failures = 0 });

    // Healthy candidate traffic: 500 requests in the window, no 5xx, p95 of 150 ms, sampled now.
    private static HttpResponseMessage HealthyPrometheusAnswer(string query)
    {
        var value = query.Contains("histogram_quantile", StringComparison.Ordinal)
            ? "150"
            : query.Contains("status_code", StringComparison.Ordinal) ? "0" : "500";
        var observedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Respond(
            HttpStatusCode.OK,
            $"{{\"status\":\"success\",\"data\":{{\"resultType\":\"vector\",\"result\":[{{\"metric\":{{}},\"value\":[{observedAt},\"{value}\"]}}]}}}}");
    }

    private static DeployProtectionState CreateObservingProtection(DateTimeOffset firstExposureAt, DateTimeOffset deadline)
        => new()
        {
            PreviousRevision = "sha256:old",
            CandidateRevision = "sha256:new",
            FirstExposureAt = firstExposureAt,
            ObservationDeadline = deadline,
            PolicyDigest = "test-digest",
            Phase = DeployProtectionPhase.Observing
        };

    private static HttpResponseMessage Respond(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// Builds an operation carrying exactly <paramref name="parameters"/> (no default telemetry keys).
    /// </summary>
    private static WorkflowOperationRecord CreateOperationWith(
        IReadOnlyDictionary<string, string> parameters,
        DateTimeOffset createdAt,
        WorkflowOperationStatus status,
        DateTimeOffset? trafficExposedAt = null,
        DeployProtectionState? protection = null)
    {
        var template = CreateOperation();
        return template with
        {
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Deploy = template.Deploy! with
            {
                Parameters = new Dictionary<string, string>(parameters, StringComparer.Ordinal),
                TrafficExposedAt = trafficExposedAt,
                Protection = protection
            }
        };
    }

    private static WorkflowOperationRecord CreateOperation(
        IReadOnlyDictionary<string, string>? extraParameters = null)
    {
        // Created comfortably past the default 2-minute warmup so the gate evaluates this cycle.
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Health-only gate: the probe short-circuits before any metrics connection is
            // resolved, so no metrics backend is required to prove the rollback path.
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.healthz.url"] = "https://example.com/healthz/ready"
        };
        if (extraParameters != null)
        {
            foreach (var (key, value) in extraParameters)
            {
                parameters[key] = value;
            }
        }

        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Reconciling,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Baking canary",
            ProviderOperationId = "recording-backend:op",
            Audit = new OperationAuditInfo
            {
                RequestedBy = "alice",
                Reason = "Canary",
                IdempotencyKey = Guid.NewGuid().ToString("N")
            },
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = "production:prod-k8s",
                RequiresExclusiveLease = true
            },
            Deploy = new DeployOperationSpec
            {
                TargetId = "prod-k8s",
                TargetKind = DeployTargetKind.Kubernetes,
                Backend = "honua-gitops-kubernetes",
                Environment = "production",
                TargetName = "honua-server",
                ArtifactReference = "ghcr.io/honua/server",
                CurrentRevision = "sha256:old",
                DesiredRevision = "sha256:new",
                Parameters = parameters
            }
        };
    }

    private sealed class FakeHealthProbe(
        DeployHealthProbeResult result,
        DeployGoldenQueryResult? goldenResult = null) : IDeployHealthProbe
    {
        public Task<DeployHealthProbeResult> ProbeAsync(DeployHealthProbeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(result);

        public Task<DeployGoldenQueryResult> ProbeGoldenQueryAsync(DeployGoldenQueryRequest request, CancellationToken cancellationToken)
            => Task.FromResult(goldenResult ?? new DeployGoldenQueryResult { Matched = true });
    }

    private sealed class OptionsMonitorStub(ControlPlaneOptions value) : IOptionsMonitor<ControlPlaneOptions>
    {
        public ControlPlaneOptions CurrentValue => value;

        public ControlPlaneOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ControlPlaneOptions, string?> listener) => null;
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class RecordingDeployBackend(
        WorkflowOperationStatus observeStatus,
        bool promotionRecommended = false) : IDeployBackend
    {
        public WorkflowOperationStatus ObserveStatus { get; set; } = observeStatus;

        public bool PromotionRecommended { get; set; } = promotionRecommended;

        public int RollbackCalls { get; private set; }

        public int PromoteCalls { get; private set; }

        public int CompleteProtectionCalls { get; private set; }

        public bool StagesCandidateWithoutTraffic { get; init; }

        public string BackendName => "honua-gitops-kubernetes";

        public DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployBackendCapabilities
            {
                SupportsRollback = true,
                SupportsProgressPolling = true,
                SupportsRevisionPinning = true,
                StagesCandidateWithoutTraffic = StagesCandidateWithoutTraffic
            });

        public Task<DeployObservation> CompleteProtectionAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            CompleteProtectionCalls++;
            return Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.Succeeded,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = "Retained recovery capacity retired."
            });
        }

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployPlan { IsReadyToSubmit = true });

        public Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = $"recording-backend:{operation.OperationId}",
                Message = "Submitted"
            });

        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = ObserveStatus,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = "Observed",
                PromotionRecommended = PromotionRecommended
            });

        public Task<DeployObservation> PromoteAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            PromoteCalls++;
            return Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.Succeeded,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = "Promoted"
            });
        }

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            return Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = "Rollback requested"
            });
        }
    }

    private sealed class SingleTargetRegistry : IDeployTargetRegistry
    {
        private static readonly DeployTargetDefinition Target = new()
        {
            TargetId = "prod-k8s",
            TargetKind = DeployTargetKind.Kubernetes,
            Backend = "honua-gitops-kubernetes",
            Environment = "production",
            TargetName = "honua-server",
            ArtifactReference = "ghcr.io/honua/server",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        };

        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>([Target]);

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult(targetId == Target.TargetId ? Target : null);
    }

    private sealed class InMemoryWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _leases = new(StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryAdd(operationId, ownerId));

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryGetValue(operationId, out var currentOwner) && currentOwner == ownerId);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        {
            _leases.TryRemove(new KeyValuePair<string, string>(operationId, ownerId));
            return Task.CompletedTask;
        }

        public Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryAdd(operation.OperationId, operation));

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowOperationRecord?>(null);

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        {
            var operations = _operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
        }
    }
}
