// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Staged-activation coverage for protected metadata releases (#4619). Every scenario drives the
/// real reconciler, activator, script executor, preflight gate and smoke checker (which queries
/// through the canonical <see cref="FeatureProviderQueryRouter"/> and evaluates the shared
/// <see cref="IAccessPolicyEvaluator"/>) over a graph store with the Postgres store's semantics:
/// store-allocated revisions, an ETag-guarded current pointer, and staged revisions that readers
/// never see. Those store semantics are proven against real PostgreSQL by
/// <c>PostgresMetadataV2GraphStoreFreshDbTests.StageAsync_KeepsReadersOnCurrent_AndActivationNeverOverwritesANewerRevision</c>.
/// Physical feature rows live outside the graph so rollback's data preservation is observable.
/// </summary>
public sealed class MetadataReleaseReconcilerTests
{
    private const string NewField = "owner_email";

    [Fact]
    public async Task Forward_StagesCandidateInvisibleToReaders_ThenActivatesAtomicallyAndCompletes()
    {
        var harness = new ReleaseHarness();
        var id = await harness.SubmitAsync();
        var prior = harness.Graph.Current;

        var staged = await harness.DriveUntilAsync(id, op => op.MetadataRelease!.CurrentStage == MetadataReleaseStage.ServicePublication);

        // Immutable prior identity captured before mutation; candidate staged from it.
        staged.MetadataRelease!.PriorRevision.Should().Be(prior.Revision);
        staged.MetadataRelease.PriorEtag.Should().Be(prior.Etag);
        var candidateRevision = staged.MetadataRelease.CandidateRevision!.Value;
        staged.MetadataRelease.CandidateEtag.Should().Be(harness.Graph.Retained(candidateRevision)!.Etag);
        staged.MetadataRelease.OwnedOperations.Should().ContainSingle(op => op.FieldName == NewField);
        harness.Graph.Current.Revision.Should().Be(prior.Revision, "canonical readers stay on the prior revision");
        ReleaseHarness.HasField(harness.Graph.Current, "parcels", NewField).Should().BeFalse();
        ReleaseHarness.HasField(harness.Graph.Retained(candidateRevision)!, "parcels", NewField).Should().BeTrue();

        var smoked = await harness.DriveUntilAsync(id, op => op.MetadataRelease!.CurrentStage == MetadataReleaseStage.MetadataApply);
        harness.Graph.Current.Revision.Should().Be(prior.Revision, "the candidate is smoke-checked before activation");
        smoked.MetadataRelease!.EvidenceRefs.Select(e => e.Kind).Should().Equal("smoke-candidate");
        harness.Physical.Queries.Should().Contain(q => q.LayerId == ReleaseHarness.ParcelsLayer && q.OutFields.SequenceEqual(new[] { NewField }),
            "the candidate smoke queries the candidate schema through the canonical provider path");

        var final = await harness.DriveToTerminalAsync(id);

        final.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        final.MetadataRelease!.CurrentStage.Should().Be(MetadataReleaseStage.Complete);
        final.MetadataRelease.ActivatedAt.Should().NotBeNull();
        final.MetadataRelease.EvidenceRefs.Select(e => e.Kind).Should().Equal("smoke-candidate", "smoke");
        harness.Graph.Current.Revision.Should().Be(candidateRevision);
        harness.Graph.CurrentHistory.Should().Equal([prior.Revision, candidateRevision], "activation is one pointer move with no intermediate state");
        harness.Stages.Should().ContainInOrder(
            MetadataReleaseStage.Backup,
            MetadataReleaseStage.ScriptMigration,
            MetadataReleaseStage.ServicePublication,
            MetadataReleaseStage.Smoke,
            MetadataReleaseStage.MetadataApply,
            MetadataReleaseStage.SloWatch,
            MetadataReleaseStage.Complete);
        harness.Physical.Rows(ReleaseHarness.ParcelsLayer).Should().OnlyContain(row => row.ContainsKey(NewField), "the qualified ETL populated the new field before activation");
    }

    [Fact]
    public async Task PreparationFailure_MissingResource_FailsWithoutAnyMutation()
    {
        var harness = new ReleaseHarness();
        var id = await harness.SubmitAsync(ReleaseHarness.Plan(resourceId: "wetlands"));

        var final = await harness.DriveToTerminalAsync(id);

        final.Status.Should().Be(WorkflowOperationStatus.Failed);
        final.MetadataRelease!.Blockers.Should().Contain(MetadataReleaseScriptExecutor.ResourceMissing);
        final.ErrorMessage.Should().Contain("live catalog is unchanged at revision 1");
        harness.Graph.RetainedRevisions.Should().Equal(1L);
        harness.Graph.CurrentHistory.Should().Equal(1L);
        harness.Etl.Calls.Should().Be(0);
    }

    [Fact]
    public async Task PreparationFailure_ConflictingExistingField_IsRejectedNotOverwritten()
    {
        var harness = new ReleaseHarness(graph => ReleaseHarness.WithField(graph, "parcels", NewField, MetadataV2FieldType.Integer));
        var id = await harness.SubmitAsync();

        var final = await harness.DriveToTerminalAsync(id);

        final.Status.Should().Be(WorkflowOperationStatus.Failed);
        final.MetadataRelease!.Blockers.Should().Contain(MetadataReleaseScriptExecutor.FieldConflict);
        harness.Graph.CurrentHistory.Should().Equal(1L);
        ReleaseHarness.FieldType(harness.Graph.Current, "parcels", NewField).Should().Be(MetadataV2FieldType.Integer);
    }

    [Fact]
    public async Task EtlFailureBeforeActivation_DiscardsCandidate_AndLeavesLiveCatalogUntouched()
    {
        var harness = new ReleaseHarness();
        harness.Etl.Failure = new InvalidOperationException("Data-populate job failed.");
        var id = await harness.SubmitAsync();

        var final = await harness.DriveToTerminalAsync(id);

        final.Status.Should().Be(WorkflowOperationStatus.Failed);
        final.MetadataRelease!.Blockers.Should().Contain("metadata-release-etl-failed");
        final.ErrorMessage.Should().Contain("candidate was discarded");
        harness.Graph.RetainedRevisions.Should().Equal([1L], "the staged candidate is cleaned up");
        harness.Graph.CurrentHistory.Should().Equal([1L], "the candidate was never exposed");
    }

    [Fact]
    public async Task CandidateSmokeFailure_DiscardsCandidate_WithoutExposingIt()
    {
        var harness = new ReleaseHarness();
        harness.Physical.Rows(ReleaseHarness.ParcelsLayer).Clear();
        var id = await harness.SubmitAsync();

        var final = await harness.DriveToTerminalAsync(id);

        final.Status.Should().Be(WorkflowOperationStatus.Failed);
        final.MetadataRelease!.Blockers.Should().Contain("metadata-release-candidate-smoke-failed");
        final.ErrorMessage.Should().Contain("candidate revision 2").And.Contain("returned no rows");
        harness.Graph.RetainedRevisions.Should().Equal(1L);
        harness.Graph.CurrentHistory.Should().Equal(1L);
    }

    [Fact]
    public async Task PostActivationRegression_RestoresPriorRevision_PreservesData_AndVerifiesRecovery()
    {
        var harness = new ReleaseHarness(faultInjection: true);
        var id = await harness.SubmitAsync();

        var requested = await harness.DriveUntilAsync(id, op => op.Status == WorkflowOperationStatus.RollbackRequested);
        requested.CurrentPhase.Should().Contain("Smoke check failed after activation of revision 2");
        harness.Graph.Current.Revision.Should().Be(2);
        var physicalBeforeRollback = harness.Physical.Snapshot(ReleaseHarness.ParcelsLayer);

        var final = await harness.DriveToTerminalAsync(id);

        final.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        final.CurrentPhase.Should().Contain("Reactivated prior revision 1").And.Contain("Recovered revision 1 verified");
        final.MetadataRelease!.EvidenceRefs.Select(e => e.Kind).Should().Equal("smoke-candidate", "smoke", "smoke-recovered");
        harness.Graph.Current.Revision.Should().Be(1);
        harness.Graph.CurrentHistory.Should().Equal(1L, 2L, 1L);
        ReleaseHarness.HasField(harness.Graph.Current, "parcels", NewField).Should().BeFalse();
        harness.Physical.Snapshot(ReleaseHarness.ParcelsLayer).Should().BeEquivalentTo(physicalBeforeRollback,
            "rollback restores metadata only; physical rows, including ETL-populated values, are untouched");
    }

    [Fact]
    public async Task RollbackAfterUnrelatedUpdateAndCommittedEdits_RevertsOnlyTheOwnedField()
    {
        var harness = new ReleaseHarness();
        var id = await harness.SubmitAsync();
        (await harness.DriveToTerminalAsync(id)).Status.Should().Be(WorkflowOperationStatus.Succeeded);

        // A later, unrelated update to a different service and a committed feature edit.
        var afterRelease = harness.Graph.Current;
        var unrelated = await harness.Graph.SaveAsync(ReleaseHarness.WithField(afterRelease.Graph, "roads", "surface", MetadataV2FieldType.String), afterRelease.Etag);
        harness.Physical.Rows(ReleaseHarness.ParcelsLayer)[0]["owner"] = "edited-after-activation";
        var physicalBeforeRollback = harness.Physical.Snapshot(ReleaseHarness.ParcelsLayer);

        await harness.RequestRollbackAsync(id);
        var final = await harness.DriveToTerminalAsync(id);

        final.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        final.CurrentPhase.Should().Contain($"Reverted 1 owned field change(s) on top of revision {unrelated.Revision}");
        var live = harness.Graph.Current;
        live.Revision.Should().BeGreaterThan(unrelated.Revision, "the revert is a new revision on top of the later update");
        ReleaseHarness.HasField(live, "parcels", NewField).Should().BeFalse();
        ReleaseHarness.HasField(live, "roads", "surface").Should().BeTrue("the unrelated update to another service survives");
        live.Graph.Services.Select(s => s.Metadata.Id).Should().BeEquivalentTo(new[] { "cadastre", "transport" });
        harness.Physical.Snapshot(ReleaseHarness.ParcelsLayer).Should().BeEquivalentTo(physicalBeforeRollback,
            "committed feature edits and physical data are preserved");
    }

    [Fact]
    public async Task Rollback_WhenFieldPredatesRelease_LeavesItInPlace()
    {
        var harness = new ReleaseHarness(graph => ReleaseHarness.WithField(graph, "parcels", NewField, MetadataV2FieldType.String));
        var id = await harness.SubmitAsync();
        var completed = await harness.DriveToTerminalAsync(id);
        completed.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        completed.MetadataRelease!.OwnedOperations.Should().BeEmpty("an identical pre-existing field is not owned by the release");

        await harness.RequestRollbackAsync(id);
        var final = await harness.DriveToTerminalAsync(id);

        final.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        ReleaseHarness.HasField(harness.Graph.Current, "parcels", NewField).Should().BeTrue("rollback never removes what the release did not add");
    }

    [Fact]
    public async Task ConcurrentUpdateToDifferentServiceBeforeActivation_IsRebasedRevalidatedAndPreserved()
    {
        var harness = new ReleaseHarness();
        var id = await harness.SubmitAsync();
        var ready = await harness.DriveUntilAsync(id, op => op.MetadataRelease!.CurrentStage == MetadataReleaseStage.MetadataApply);
        var firstCandidate = ready.MetadataRelease!.CandidateRevision!.Value;

        var current = harness.Graph.Current;
        var concurrent = await harness.Graph.SaveAsync(ReleaseHarness.WithField(current.Graph, "roads", "surface", MetadataV2FieldType.String), current.Etag);

        var final = await harness.DriveToTerminalAsync(id);

        final.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        final.MetadataRelease!.RebaseCount.Should().Be(1);
        final.MetadataRelease.PriorRevision.Should().Be(concurrent.Revision);
        final.MetadataRelease.Warnings.Should().ContainSingle(w => w.Contains($"onto concurrent revision {concurrent.Revision}", StringComparison.Ordinal));
        final.MetadataRelease.EvidenceRefs.Count(e => e.Kind == "smoke-candidate").Should().Be(2, "the rebased candidate is revalidated");
        var live = harness.Graph.Current;
        ReleaseHarness.HasField(live, "roads", "surface").Should().BeTrue("the concurrent update is never overwritten");
        ReleaseHarness.HasField(live, "parcels", NewField).Should().BeTrue();
        harness.Graph.Retained(firstCandidate).Should().BeNull("the superseded candidate is discarded");
        harness.Graph.CurrentHistory.Should().Equal(1L, concurrent.Revision, live.Revision);
    }

    [Fact]
    public async Task EtagConflictWithIncompatibleConcurrentChange_IsRejectedAndNeverOverwritten()
    {
        var harness = new ReleaseHarness();
        var id = await harness.SubmitAsync();
        await harness.DriveUntilAsync(id, op => op.MetadataRelease!.CurrentStage == MetadataReleaseStage.MetadataApply);

        var current = harness.Graph.Current;
        var concurrent = await harness.Graph.SaveAsync(ReleaseHarness.WithField(current.Graph, "parcels", NewField, MetadataV2FieldType.Integer), current.Etag);

        var final = await harness.DriveToTerminalAsync(id);

        final.Status.Should().Be(WorkflowOperationStatus.Failed);
        final.MetadataRelease!.Blockers.Should().Contain(MetadataReleaseScriptExecutor.FieldConflict);
        harness.Graph.Current.Revision.Should().Be(concurrent.Revision);
        ReleaseHarness.FieldType(harness.Graph.Current, "parcels", NewField).Should().Be(MetadataV2FieldType.Integer);
    }

    [Fact]
    public async Task PersistentConcurrentWriters_ExhaustRebases_AndActivationIsRejected()
    {
        var harness = new ReleaseHarness();
        var writes = 0;
        harness.Graph.BeforeActivate = async () =>
        {
            var current = harness.Graph.Current;
            await harness.Graph.SaveAsync(ReleaseHarness.WithField(current.Graph, "roads", $"note_{++writes}", MetadataV2FieldType.String), current.Etag);
        };
        var id = await harness.SubmitAsync();

        var final = await harness.DriveToTerminalAsync(id);

        final.Status.Should().Be(WorkflowOperationStatus.Failed);
        final.MetadataRelease!.Blockers.Should().Contain("metadata-release-activation-conflict");
        final.MetadataRelease.RebaseCount.Should().Be(MetadataReleaseReconciler.MaxRebaseAttempts);
        ReleaseHarness.HasField(harness.Graph.Current, "parcels", NewField).Should().BeFalse();
        Enumerable.Range(1, writes).Should().OnlyContain(n => ReleaseHarness.HasField(harness.Graph.Current, "roads", $"note_{n}"),
            "every concurrent update survives");
    }

    [Fact]
    public async Task CrashAfterActivationBeforeRecordWrite_ResumesWithoutReactivating()
    {
        var harness = new ReleaseHarness();
        var id = await harness.SubmitAsync();
        var beforeActivation = await harness.DriveUntilAsync(id, op => op.MetadataRelease!.CurrentStage == MetadataReleaseStage.MetadataApply);

        await harness.StepAsync(id);
        harness.Graph.Current.Revision.Should().Be(beforeActivation.MetadataRelease!.CandidateRevision!.Value);
        await harness.Workflow.SetAsync(beforeActivation); // the record write was lost in the crash

        var final = await harness.DriveToTerminalAsync(id);

        final.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        harness.Graph.CurrentHistory.Should().Equal(1L, beforeActivation.MetadataRelease.CandidateRevision!.Value);
    }

    [Fact]
    public async Task CrashAfterStagingBeforeRecordWrite_ResumesWithoutLiveExposure()
    {
        var harness = new ReleaseHarness();
        var id = await harness.SubmitAsync();
        var beforeStaging = await harness.DriveUntilAsync(id, op => op.MetadataRelease!.CurrentStage == MetadataReleaseStage.ScriptMigration);

        await harness.StepAsync(id);
        var orphan = harness.Graph.RetainedRevisions.Max();
        await harness.Workflow.SetAsync(beforeStaging);

        var final = await harness.DriveToTerminalAsync(id);

        final.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        harness.Graph.CurrentHistory.Should().Equal(1L, final.MetadataRelease!.CandidateRevision!.Value);
        harness.Graph.CurrentHistory.Should().NotContain(orphan, "an unrecorded staged revision is inert and never becomes current");
    }

    [Fact]
    public async Task CrashDuringRollback_ResumesIdempotentlyToVerifiedRolledBack()
    {
        var harness = new ReleaseHarness(faultInjection: true);
        var id = await harness.SubmitAsync();
        var requested = await harness.DriveUntilAsync(id, op => op.Status == WorkflowOperationStatus.RollbackRequested);

        await harness.StepAsync(id);
        harness.Graph.Current.Revision.Should().Be(1);
        await harness.Workflow.SetAsync(requested);

        var final = await harness.DriveToTerminalAsync(id);

        final.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        final.CurrentPhase.Should().Contain("nothing to revert");
        harness.Graph.CurrentHistory.Should().Equal(1L, 2L, 1L);
    }

    [Theory]
    [InlineData("drop-existing-field", MetadataReleaseChangePolicy.DestructiveChange)]
    [InlineData("non-nullable-add", MetadataReleaseChangePolicy.NonNullableAdd)]
    [InlineData("etl-declares-no-fields", MetadataReleaseChangePolicy.EtlUnprovenCompensation)]
    [InlineData("etl-writes-existing-field", MetadataReleaseChangePolicy.EtlUnprovenCompensation)]
    [InlineData("inverse-drops-unowned-field", MetadataReleaseChangePolicy.InverseNotOwned)]
    public async Task ChangePolicy_RejectsDestructiveOrUncompensatedChanges_BeforeMutation(string scenario, string blocker)
    {
        var plan = scenario switch
        {
            "drop-existing-field" => ReleaseHarness.Plan(kind: MetadataReleaseScriptOperationKind.DropColumn, field: "owner"),
            "non-nullable-add" => ReleaseHarness.Plan(nullable: false),
            "etl-declares-no-fields" => ReleaseHarness.Plan(etlFields: []),
            "etl-writes-existing-field" => ReleaseHarness.Plan(etlFields: ["owner"]),
            _ => ReleaseHarness.Plan(inverseField: "owner"),
        };
        var harness = new ReleaseHarness();
        var id = await harness.SubmitAsync(plan);

        var final = await harness.StepAsync(id);

        final.Status.Should().Be(WorkflowOperationStatus.Failed);
        final.MetadataRelease!.Blockers.Should().Contain(blocker);
        final.ErrorMessage.Should().Contain("before any mutation");
        harness.Graph.RetainedRevisions.Should().Equal(1L);
        harness.Etl.Calls.Should().Be(0);
    }

    [Fact]
    public async Task CandidateSmoke_DetectsAuthorizationAndRenderingRegressions()
    {
        var harness = new ReleaseHarness(graph => ReleaseHarness.WithDisplayField(graph, "parcels", "owner"));
        var plan = ReleaseHarness.Plan();
        var baseline = harness.Graph.Current;

        var identical = await harness.Graph.StageAsync(baseline.Graph);
        var anonymous = await harness.Graph.StageAsync(ReleaseHarness.WithResourcePolicy(baseline.Graph, "parcels", new AccessPolicy { AllowAnonymous = true }));
        var dropsDisplayField = await harness.Graph.StageAsync(ReleaseHarness.WithoutField(baseline.Graph, "parcels", "owner"));

        var ok = await harness.Smoke.RunAsync(plan, Candidate(identical.Revision, baseline.Revision));
        var authDrift = await harness.Smoke.RunAsync(plan, Candidate(anonymous.Revision, baseline.Revision));
        var renderDrift = await harness.Smoke.RunAsync(plan, Candidate(dropsDisplayField.Revision, baseline.Revision));

        ok.Passed.Should().BeTrue(ok.Message);
        ok.RowCount.Should().Be(3);
        authDrift.Passed.Should().BeFalse();
        authDrift.Message.Should().Contain("changes authorization").And.Contain("anonymous read was denied and is now allowed");
        renderDrift.Passed.Should().BeFalse();
        renderDrift.Message.Should().Contain("display field 'owner'");

        static MetadataReleaseSmokeRequest Candidate(long revision, long baselineRevision) => new()
        {
            Phase = MetadataReleaseSmokePhase.Candidate,
            Revision = revision,
            BaselineRevision = baselineRevision,
            ExpectNewField = false
        };
    }

    [Fact]
    public async Task ReconcileAsync_WhenSnapshotRequired_RefusesAtPreflight()
    {
        var harness = new ReleaseHarness(gate: new FakePreflightGate(MetadataRollbackReadinessClassification.SnapshotRequired));
        var id = await harness.SubmitAsync();

        var refused = await harness.StepAsync(id);

        refused.Status.Should().Be(WorkflowOperationStatus.Failed);
        refused.MetadataRelease!.CurrentStage.Should().Be(MetadataReleaseStage.Failed);
        refused.ErrorMessage.Should().Contain("snapshot");
        refused.BlockingReasons.Should().Contain("metadata-release-snapshot-not-implemented");
        harness.Graph.RetainedRevisions.Should().Equal([1L], "no side effect ran behind the refusal");
    }

    [Fact]
    public async Task ReconcileAsync_WhenRollbackRequestedWithSnapshotClass_RefusesExecution()
    {
        var harness = new ReleaseHarness();
        var id = await harness.SubmitAsync();
        var operation = (await harness.Workflow.GetAsync(id))!;
        await harness.Workflow.SetAsync(operation with
        {
            Status = WorkflowOperationStatus.RollbackRequested,
            MetadataRelease = operation.MetadataRelease! with
            {
                CurrentStage = MetadataReleaseStage.RollbackRequested,
                RollbackPlan = new MetadataRollbackPlan { Class = MetadataRollbackClass.SnapshotRestore }
            }
        });

        var refused = await harness.StepAsync(id);

        refused.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
        refused.ErrorMessage.Should().Contain("Snapshot rollback is not yet implemented");
        harness.Graph.CurrentHistory.Should().Equal(1L);
    }

    [Fact]
    public async Task ReconcileAsync_WhenAlreadyComplete_IsIdempotentAndRunsNoSideEffect()
    {
        var harness = new ReleaseHarness();
        var id = await harness.SubmitAsync();
        await harness.DriveToTerminalAsync(id);
        var history = harness.Graph.CurrentHistory.ToArray();
        var etlCalls = harness.Etl.Calls;

        var unchanged = await harness.StepAsync(id);

        unchanged.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        harness.Graph.CurrentHistory.Should().Equal(history);
        harness.Etl.Calls.Should().Be(etlCalls);
    }

    // ---- harness ---------------------------------------------------------

    private sealed class ReleaseHarness
    {
        public const int ParcelsLayer = 11;
        public const int RoadsLayer = 12;

        public ReleaseHarness(
            Func<MetadataV2Graph, MetadataV2Graph>? shapeGraph = null,
            bool faultInjection = false,
            IMetadataReleasePreflightGate? gate = null)
        {
            Graph = new StagedGraphStore((shapeGraph ?? (graph => graph))(BaselineGraph()));

            var services = new ServiceCollection();
            services.AddSingleton<IMetadataV2GraphStore>(Graph);
            services.AddSingleton<IMetadataV2GraphProvider>(Graph);
            services.AddSingleton<IAccessPolicyEvaluator>(new AccessPolicyEvaluator());
            services.AddSingleton<IFeatureDataProviderRegistry>(new FeatureDataProviderRegistry([Physical]));
            services.AddSingleton(sp => new FeatureProviderQueryRouter(
                Mock.Of<ISecureConnectionRegistry>(),
                sp.GetRequiredService<IFeatureDataProviderRegistry>(),
                DataProviderNames.Postgis));
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            var options = new MetadataReleaseOperationOptions();
            options.FaultInjection.Enabled = faultInjection;
            options.FaultInjection.ForceSmokeFailure = faultInjection;

            Smoke = new MetadataReleaseSmokeChecker(scopeFactory, Options.Create(options));
            Etl = new PopulatingEtl(Physical);
            Reconciler = new MetadataReleaseReconciler(
                Workflow,
                gate ?? new MetadataReleasePreflightGate(scopeFactory),
                new MetadataReleaseScriptExecutor(NullLogger<MetadataReleaseScriptExecutor>.Instance),
                Etl,
                new MetadataReleaseActivator(scopeFactory),
                Smoke,
                NullLogger<MetadataReleaseReconciler>.Instance);
        }

        public InMemoryWorkflowOperationStore Workflow { get; } = new();

        public StagedGraphStore Graph { get; }

        public PhysicalFeatureProvider Physical { get; } = new();

        public PopulatingEtl Etl { get; }

        public MetadataReleaseSmokeChecker Smoke { get; }

        public MetadataReleaseReconciler Reconciler { get; }

        public List<MetadataReleaseStage> Stages { get; } = [];

        public static MetadataReleaseExecutionPlan Plan(
            string resourceId = "parcels",
            MetadataReleaseScriptOperationKind kind = MetadataReleaseScriptOperationKind.AddColumn,
            string field = NewField,
            bool nullable = true,
            IReadOnlyList<string>? etlFields = null,
            string? inverseField = null)
            => new()
            {
                PackageId = "pkg-4619",
                TargetEnvironment = "staging",
                ResourceSemanticId = resourceId,
                NewFieldName = NewField,
                DataPopulateWorkloadId = "populate-owner-email",
                DataPopulateFields = etlFields ?? [NewField],
                Script = new MetadataReleaseScript
                {
                    ScriptId = "add-owner-email",
                    ForwardOperations =
                    [
                        new MetadataReleaseScriptOperation
                        {
                            Kind = kind,
                            ResourceSemanticId = resourceId,
                            FieldName = field,
                            FieldType = "String",
                            Nullable = nullable
                        }
                    ],
                    InverseOperations = inverseField is null
                        ? []
                        : [new MetadataReleaseScriptOperation { Kind = MetadataReleaseScriptOperationKind.DropColumn, ResourceSemanticId = resourceId, FieldName = inverseField }]
                }
            };

        public async Task<string> SubmitAsync(MetadataReleaseExecutionPlan? plan = null)
        {
            plan ??= Plan();
            var now = DateTimeOffset.UtcNow;
            var operation = new WorkflowOperationRecord
            {
                OperationId = $"metadata-release-{Guid.NewGuid():N}",
                Kind = WorkflowOperationKind.MetadataRelease,
                Status = WorkflowOperationStatus.Submitted,
                CreatedAt = now,
                UpdatedAt = now,
                CurrentPhase = "submitted",
                MetadataRelease = new MetadataReleaseContext
                {
                    PackageId = plan.PackageId,
                    DesiredRevision = plan.PackageId,
                    TargetEnvironment = plan.TargetEnvironment,
                    CurrentStage = MetadataReleaseStage.Preflight,
                    ExecutionPlan = plan
                }
            };
            await Workflow.TryCreateAsync(operation);
            return operation.OperationId;
        }

        public async Task<WorkflowOperationRecord> StepAsync(string id)
        {
            await Reconciler.ReconcileMetadataReleaseAsync(id);
            var operation = (await Workflow.GetAsync(id))!;
            Stages.Add(operation.MetadataRelease!.CurrentStage);
            return operation;
        }

        public async Task<WorkflowOperationRecord> DriveUntilAsync(string id, Func<WorkflowOperationRecord, bool> predicate)
        {
            for (var i = 0; i < 40; i++)
            {
                var operation = await StepAsync(id);
                if (predicate(operation))
                {
                    return operation;
                }

                if (IsTerminal(operation.Status))
                {
                    throw new InvalidOperationException($"Operation ended {operation.Status} before the expected state: {operation.CurrentPhase}");
                }
            }

            throw new InvalidOperationException("Operation did not reach the expected state within 40 cycles.");
        }

        public Task<WorkflowOperationRecord> DriveToTerminalAsync(string id)
            => DriveUntilAsync(id, operation => IsTerminal(operation.Status));

        public async Task RequestRollbackAsync(string id)
        {
            // Same transition the operator endpoint and coordinated rollback write.
            var operation = (await Workflow.GetAsync(id))!;
            await Workflow.SetAsync(operation with
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                CompletedAt = null,
                MetadataRelease = operation.MetadataRelease! with { CurrentStage = MetadataReleaseStage.RollbackRequested }
            });
        }

        public static bool HasField(MetadataV2GraphSnapshot snapshot, string resourceId, string field)
            => FieldType(snapshot, resourceId, field) is not null;

        public static MetadataV2FieldType? FieldType(MetadataV2GraphSnapshot snapshot, string resourceId, string field)
            => snapshot.Index.ResourcesById[resourceId].SchemaFields
                .FirstOrDefault(candidate => string.Equals(candidate.Name, field, StringComparison.OrdinalIgnoreCase))?.Type;

        public static MetadataV2Graph WithField(MetadataV2Graph graph, string resourceId, string field, MetadataV2FieldType type)
            => MapResource(graph, resourceId, resource => resource with
            {
                SchemaFields = [.. resource.SchemaFields, new MetadataV2Field { Name = field, Type = type, Nullable = true }]
            });

        public static MetadataV2Graph WithoutField(MetadataV2Graph graph, string resourceId, string field)
            => MapResource(graph, resourceId, resource => resource with
            {
                SchemaFields = resource.SchemaFields.Where(candidate => candidate.Name != field).ToArray()
            });

        public static MetadataV2Graph WithDisplayField(MetadataV2Graph graph, string resourceId, string field)
            => MapResource(graph, resourceId, resource => resource with { Display = new MetadataV2ResourceDisplay { DisplayField = field } });

        public static MetadataV2Graph WithResourcePolicy(MetadataV2Graph graph, string resourceId, AccessPolicy policy)
            => MapResource(graph, resourceId, resource => resource with { AccessPolicy = policy });

        private static MetadataV2Graph MapResource(MetadataV2Graph graph, string resourceId, Func<MetadataV2Resource, MetadataV2Resource> map)
            => graph with
            {
                Resources = graph.Resources.Select(resource => resource.Metadata.Id == resourceId ? map(resource) : resource).ToArray()
            };

        private static bool IsTerminal(WorkflowOperationStatus status)
            => status is WorkflowOperationStatus.Succeeded
                or WorkflowOperationStatus.Failed
                or WorkflowOperationStatus.RolledBack
                or WorkflowOperationStatus.ManualInterventionRequired;

        private static MetadataV2Graph BaselineGraph()
            => new TestMetadataV2GraphBuilder()
                .WithEnvironment("staging")
                .AddResource(
                    "parcels",
                    "Parcels",
                    fields:
                    [
                        new MetadataV2Field { Name = "parcel_id", Type = MetadataV2FieldType.String },
                        new MetadataV2Field { Name = "owner", Type = MetadataV2FieldType.String, Nullable = true }
                    ],
                    accessPolicy: new AccessPolicy { AllowedRoles = ["cadastre-editor"] })
                .AddStorageBinding("parcels-table", "parcels", "public.parcels", storageLayerId: ParcelsLayer)
                .AddService("cadastre", "Cadastre", accessPolicy: new AccessPolicy { AllowAnonymous = true })
                .AddPublication("cadastre-parcels", "cadastre", "parcels", layerIndex: 0, storageBindingId: "parcels-table")
                .AddResource("roads", "Roads", fields: [new MetadataV2Field { Name = "road_id", Type = MetadataV2FieldType.String }])
                .AddStorageBinding("roads-table", "roads", "public.roads", storageLayerId: RoadsLayer)
                .AddService("transport", "Transport")
                .AddPublication("transport-roads", "transport", "roads", layerIndex: 0, storageBindingId: "roads-table")
                .Build();
    }

    /// <summary>
    /// Graph store with the Postgres store's semantics: store-allocated revisions above every
    /// retained snapshot, an ETag-guarded current pointer, staged revisions that do not move it, and
    /// a typed concurrency exception. Records every value the current pointer takes.
    /// </summary>
    private sealed class StagedGraphStore : IMetadataV2GraphStore, IMetadataV2GraphRevisionStager
    {
        private readonly object _gate = new();
        private readonly SortedDictionary<long, MetadataV2GraphSnapshot> _revisions = [];
        private readonly List<long> _currentHistory = [];
        private long _current;

        public StagedGraphStore(MetadataV2Graph initial)
        {
            _current = Persist(initial).Revision;
            _currentHistory.Add(_current);
        }

        /// <summary>
        /// Invoked inside activation before the ETag check, to land a concurrent writer first.
        /// </summary>
        public Func<Task>? BeforeActivate { get; set; }

        public MetadataV2GraphSnapshot Current
        {
            get
            {
                lock (_gate)
                {
                    return _revisions[_current];
                }
            }
        }

        public IReadOnlyList<long> CurrentHistory
        {
            get
            {
                lock (_gate)
                {
                    return _currentHistory.ToArray();
                }
            }
        }

        public IReadOnlyList<long> RetainedRevisions
        {
            get
            {
                lock (_gate)
                {
                    return _revisions.Keys.ToArray();
                }
            }
        }

        public MetadataV2GraphSnapshot? Retained(long revision)
        {
            lock (_gate)
            {
                return _revisions.GetValueOrDefault(revision);
            }
        }

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => new(Current);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => new(Retained(revision));

        public Task<MetadataV2GraphSnapshot> SaveAsync(MetadataV2Graph graph, string? expectedEtag, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                EnsureCurrent(expectedEtag);
                var snapshot = Persist(graph);
                MoveCurrent(snapshot.Revision);
                return Task.FromResult(snapshot);
            }
        }

        public async Task<MetadataV2GraphSnapshot> ActivateRevisionAsync(long revision, string? expectedCurrentEtag, CancellationToken cancellationToken = default)
        {
            if (BeforeActivate is { } concurrentWriter)
            {
                await concurrentWriter();
            }

            lock (_gate)
            {
                EnsureCurrent(expectedCurrentEtag);
                var target = _revisions.GetValueOrDefault(revision)
                    ?? throw new InvalidOperationException($"Metadata v2 revision {revision} is not retained.");
                MoveCurrent(revision);
                return target;
            }
        }

        public Task<MetadataV2GraphSnapshot> StageAsync(MetadataV2Graph graph, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(Persist(graph));
            }
        }

        public Task<bool> DiscardStagedAsync(long revision, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (revision == _current)
                {
                    throw new InvalidOperationException($"Metadata v2 revision {revision} is current and cannot be discarded.");
                }

                return Task.FromResult(_revisions.Remove(revision));
            }
        }

        private MetadataV2GraphSnapshot Persist(MetadataV2Graph graph)
        {
            var revision = (_revisions.Count == 0 ? 0 : _revisions.Keys.Max()) + 1;
            var snapshot = new MetadataV2GraphSnapshot(graph with { Revision = revision }, $"\"r{revision}-{Guid.NewGuid():N}\"", DateTimeOffset.UtcNow);
            _revisions[revision] = snapshot;
            return snapshot;
        }

        private void EnsureCurrent(string? expectedEtag)
        {
            var actual = _revisions[_current].Etag;
            if (expectedEtag is not null && !string.Equals(expectedEtag, actual, StringComparison.Ordinal))
            {
                throw new MetadataV2GraphConcurrencyException("Metadata v2 etag mismatch.", expectedEtag, actual);
            }
        }

        private void MoveCurrent(long revision)
        {
            _current = revision;
            _currentHistory.Add(revision);
        }
    }

    /// <summary>
    /// Physical feature rows per storage layer, served through the canonical provider router.
    /// Independent of the metadata graph, so data preservation across rollback is observable.
    /// </summary>
    private sealed class PhysicalFeatureProvider : IFeatureDataProvider
    {
        private readonly Dictionary<int, List<Dictionary<string, object?>>> _rows = new()
        {
            [ReleaseHarness.ParcelsLayer] =
            [
                new() { ["parcel_id"] = "P-1", ["owner"] = "Ada" },
                new() { ["parcel_id"] = "P-2", ["owner"] = "Grace" },
                new() { ["parcel_id"] = "P-3", ["owner"] = null },
            ],
            [ReleaseHarness.RoadsLayer] =
            [
                new() { ["road_id"] = "R-1" },
            ],
        };

        public PhysicalFeatureProvider()
        {
            var reader = new Mock<IFeatureReader>();
            reader
                .Setup(r => r.QueryAsync(It.IsAny<int>(), It.IsAny<FeatureQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int layerId, FeatureQuery query, CancellationToken _) =>
                {
                    Queries.Add((layerId, query.OutFields?.ToArray() ?? []));
                    var rows = Rows(layerId);
                    var items = rows
                        .Take(query.Limit ?? rows.Count)
                        .Select((row, index) => Feature.Create(index + 1, null, row.ToImmutableDictionary()))
                        .ToImmutableArray();
                    return QueryResult<Feature>.Create(rows.Count, items);
                });
            Reader = reader.Object;
        }

        public ConcurrentBag<(int LayerId, string[] OutFields)> Queries { get; } = [];

        public string ProviderName => DataProviderNames.Postgis;

        public FeatureProviderCapabilities Capabilities => FeatureProviderCapabilities.ReadWritePostgis;

        public IFeatureReader Reader { get; }

        public IFeatureWriter? Writer => null;

        public List<Dictionary<string, object?>> Rows(int layerId) => _rows[layerId];

        public Dictionary<string, object?>[] Snapshot(int layerId)
            => Rows(layerId).Select(row => new Dictionary<string, object?>(row)).ToArray();
    }

    /// <summary>
    /// Qualified additive ETL: writes values only into the fields the plan declares.
    /// </summary>
    private sealed class PopulatingEtl(PhysicalFeatureProvider physical) : IMetadataReleaseDataJobDispatcher
    {
        public int Calls { get; private set; }

        public Exception? Failure { get; set; }

        public Task<bool> DispatchAndAwaitAsync(MetadataReleaseExecutionPlan plan, string operationId, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Failure is not null)
            {
                throw Failure;
            }

            var rows = physical.Rows(ReleaseHarness.ParcelsLayer);
            for (var i = 0; i < rows.Count; i++)
            {
                foreach (var field in plan.DataPopulateFields)
                {
                    rows[i][field] = $"owner{i + 1}@example.test";
                }
            }

            return Task.FromResult(true);
        }
    }

    private sealed class FakePreflightGate(MetadataRollbackReadinessClassification classification) : IMetadataReleasePreflightGate
    {
        public Task<MetadataReleasePreflightResult> EvaluateAsync(MetadataReleaseExecutionPlan plan, CancellationToken cancellationToken = default)
            => Task.FromResult(new MetadataReleasePreflightResult
            {
                CanProceed = true,
                RollbackClassification = classification,
                Reason = "test classification"
            });
    }

    private sealed class InMemoryWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _metadataPackageIndex = new(StringComparer.Ordinal);
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
        {
            var created = _operations.TryAdd(operation.OperationId, operation);
            if (created)
            {
                Index(operation);
            }

            return Task.FromResult(created);
        }

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult(
                _metadataPackageIndex.TryGetValue(packageId, out var operationId) &&
                _operations.TryGetValue(operationId, out var operation)
                    ? operation
                    : null);

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            Index(operation);
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            Index(operation);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        {
            var operations = _operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
        }

        private void Index(WorkflowOperationRecord operation)
        {
            if (operation.Kind == WorkflowOperationKind.MetadataRelease &&
                !string.IsNullOrWhiteSpace(operation.MetadataRelease?.PackageId))
            {
                _metadataPackageIndex[operation.MetadataRelease.PackageId] = operation.OperationId;
            }
        }
    }
}
