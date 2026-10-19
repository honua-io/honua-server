// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

// Controlled observer and job-store ports test adapter boundaries. These cases do
// not qualify lease cryptography, actual queue acceptance, or an imported dataset.
public sealed class DatasetImportSubmissionAdapterTests
{
    [UnitTest]
    public async Task AcceptedHistoricalHumanStatusUsesActualOutputAndNeverRestoresExpiredExecutorOrSource()
    {
        using var fixture = new Fixture();
        var receipt = await fixture.Service.ReadAsync(fixture.Context, fixture.Reference);
        receipt!.State.Should().Be("accepted");
        receipt.Job!.JobId.Should().Be(fixture.Job.OperationId);
        receipt.Job.Output!.PhysicalTableName.Should().Be("actual_physical_table");
        receipt.Job.Output.PhysicalTableName.Should().NotBe(fixture.Task.Destination.Table);
        receipt.Job.OutcomePending.Should().BeFalse();
        fixture.Observed.Should().BeSameAs(fixture.Context.User);
        await fixture.Tasks.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!, default!, default!, default, default!, default);
        JsonSerializer.Serialize(receipt, AgentWorkspaceJsonContext.Default.DatasetImportSubmissionReceipt).Should().NotContain("sealed-private-authority");
    }

    [UnitTheory]
    [InlineData(ExecutionJobStatus.Queued)]
    [InlineData(ExecutionJobStatus.Provisioning)]
    [InlineData(ExecutionJobStatus.Running)]
    public async Task UnresolvedAcceptedImportExposesSafePendingOutcomeWithoutClaimingCompletion(ExecutionJobStatus status)
    {
        using var fixture = new Fixture();
        fixture.CurrentJob = fixture.Job with
        {
            Status = status,
            CompletedAt = null,
            FileImportOutput = null,
            CancellationRequestedAt = DateTimeOffset.UtcNow,
            CommitRecovery = new(JobExecutionLeaseIntentFingerprint.Compute(fixture.Job), "private-worker", 2, DateTimeOffset.UtcNow)
            {
                FailureMessage = "private-reconciliation-detail",
                DeferredStatus = ExecutionJobStatus.Cancelled
            }
        };
        var receipt = await fixture.Service.ReadAsync(fixture.Context, fixture.Reference);
        receipt!.State.Should().Be("accepted");
        receipt.Job!.Status.Should().Be(status.ToString().ToLowerInvariant());
        receipt.Job.OutcomePending.Should().BeTrue();
        receipt.Job.Output.Should().BeNull();
        receipt.Job.CompletedAt.Should().BeNull();
        receipt.Job.ErrorCode.Should().BeNull();
        var json = JsonSerializer.Serialize(receipt, AgentWorkspaceJsonContext.Default.DatasetImportSubmissionReceipt);
        json.Should().Contain("\"outcomePending\":true");
        json.Should().NotContain("private-worker").And.NotContain("private-reconciliation-detail").And.NotContain("sealed-private-authority");
    }

    [UnitTheory]
    [InlineData("membership")]
    [InlineData("target")]
    [InlineData("task-deleted")]
    [InlineData("deadline")]
    [InlineData("prepared-authority")]
    [InlineData("prepared-deadline")]
    [InlineData("turn-selection")]
    [InlineData("job-deleted")]
    [InlineData("job-version")]
    public async Task DelayedObservationCannotExposeReceiptAfterOriginalScopeChanges(string change)
    {
        using var fixture = new Fixture();
        fixture.DuringObservation = () =>
        {
            switch (change)
            {
                case "job-deleted": fixture.JobPresent = false; break;
                case "job-version": fixture.CurrentJob = fixture.Job with { Version = fixture.Job.Version + 1 }; break;
                case "membership": fixture.Document = fixture.Document with { Members = ["executor"], OwnerId = "other-human" }; break;
                case "target": fixture.Target = "other-target"; break;
                case "task-deleted": fixture.Document = fixture.Document with { DatasetImportTasks = [] }; break;
                case "deadline": fixture.Document = fixture.Document with { DatasetImportTasks = new() { [fixture.Task.TaskId] = [fixture.Task with { UploadAdmissionDeadline = fixture.Task.UploadAdmissionDeadline.AddHours(1) }] } }; break;
                case "prepared-authority": fixture.Document = fixture.Document with { DatasetImportPreparedAdmissions = new() { [fixture.Task.TaskId] = fixture.Prepared with { AuthorityFingerprint = new string('d', 64) } } }; break;
                case "prepared-deadline": fixture.Document = fixture.Document with { DatasetImportPreparedAdmissions = new() { [fixture.Task.TaskId] = fixture.Prepared with { AcceptBefore = fixture.Prepared.AcceptBefore.AddHours(1) } } }; break;
                case "turn-selection": fixture.Document.Turns["turn"] = fixture.Document.Turns["turn"] with { Prompt = "changed-original-input" }; break;
            }
        };
        (await fixture.Service.ReadAsync(fixture.Context, fixture.Reference)).Should().BeNull();
    }

    [UnitTheory]
    [InlineData("job")]
    [InlineData("lease")]
    [InlineData("intent")]
    [InlineData("task")]
    [InlineData("authority")]
    [InlineData("policy")]
    public async Task PreparedOperationNeverSubstitutesForExactCanonicalAcceptedObservation(string change)
    {
        using var fixture = new Fixture();
        fixture.Observation = fixture.Observation with
        {
            JobId = change == "job" ? "foreign-job" : fixture.Observation.JobId,
            LeaseId = change == "lease" ? "foreign-lease" : fixture.Observation.LeaseId,
            IntentFingerprint = change == "intent" ? new string('c', 64) : fixture.Observation.IntentFingerprint,
            Admission = fixture.Observation.Admission! with
            {
                TaskId = change == "task" ? "foreign-task" : fixture.Task.TaskId,
                AuthorityRevision = change == "authority" ? 99 : fixture.Prepared.AuthorityRevision,
                PolicyContextFingerprint = change == "policy" ? new string('e', 64) : fixture.Prepared.PolicyContextFingerprint
            }
        };
        var act = () => fixture.Service.ReadAsync(fixture.Context, fixture.Reference);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [UnitTest]
    public async Task MissingActualJobIsNotAcceptanceEvenWhenPreparationExists()
    {
        using var fixture = new Fixture();
        fixture.JobPresent = false;
        var contribution = new DatasetImportContribution(fixture.Task, "executor", fixture.Human, fixture.Human, "turn",
            fixture.Prepared.OriginalTurnHash, fixture.Prepared.AuthorityRevision, fixture.Prepared.AuthorityFingerprint, fixture.Prepared.PolicyContextFingerprint);
        fixture.Contributors.AuthorizeAsync(fixture.Context.User, fixture.Task.WorkspaceId, fixture.Task.TaskId, 1, fixture.Task.ContentHash,
            false, Arg.Any<CancellationToken>()).Returns(contribution);
        var receipt = await fixture.Service.ReadAsync(fixture.Context, fixture.Reference);
        receipt!.State.Should().Be("prepared"); receipt.Job.Should().BeNull();
        await fixture.Leases.DidNotReceiveWithAnyArgs().ResolveForObservationAsync(default!, default!, default);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services;
        internal ClaimsPrincipal? Observed;
        internal Action? DuringObservation;
        internal bool JobPresent = true;
        internal string Target = "target";
        internal AgentWorkspaceDocument Document;
        internal readonly DatasetImportTask Task;
        internal readonly DatasetImportPreparedAdmission Prepared;
        internal readonly ExecutionJobRecord Job;
        internal ExecutionJobRecord CurrentJob;
        internal JobExecutionLeaseObservation Observation;
        internal readonly IDatasetImportTaskProvider Tasks = Substitute.For<IDatasetImportTaskProvider>();
        internal readonly IDatasetImportContributorAuthorizer Contributors = Substitute.For<IDatasetImportContributorAuthorizer>();
        internal readonly IJobExecutionLeaseAuthority Leases = Substitute.For<IJobExecutionLeaseAuthority>();
        internal readonly DefaultHttpContext Context;
        internal readonly DatasetImportSubmissionService Service;
        internal readonly ReadDatasetImportAuthority Reference;
        internal readonly string Human;

        internal Fixture()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "human")], "Bearer");
            CanonicalSecurityActor.StampAuthorityClaims(identity);
            var principal = new ClaimsPrincipal(identity); Human = CanonicalSecurityActor.Resolve(principal)!.ActorId;
            var now = DateTimeOffset.UtcNow;
            Task = new("task", 1, "", "executor", "tenant", "target", "workspace", new("public", "requested_table"),
                new("browser-file", "places.geojson", 123, new string('a', 64)), new(), now.AddHours(-1));
            Task = Task with { ContentHash = DatasetImportTaskState.ContentHash(Task) };
            var captured = new AgentWorkspaceContext("workspace", "target", "view", 0, [], null, "tenant");
            var turn = new AgentWorkspaceTurn("turn", Human, "Import original file", captured, null);
            var turnHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(turn, AgentWorkspaceJsonContext.Default.AgentWorkspaceTurn)));
            var credential = new AgentCredentialSnapshot { Kind = "api-key", Id = "deleted-key", Permissions = [], TokenExpiresAt = now.AddDays(-2) };
            var policy = new JobSecurityContext("executor", "tenant", []) { OwnerActorId = "executor", WorkspaceOwnerId = Human, AgentCredential = credential };
            var credentialHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(credential, AgentWorkspaceJsonContext.Default.AgentCredentialSnapshot)));
            var policyHash = JobSecurityContextFingerprint.Compute(policy);
            Prepared = new(Task.TaskId, 1, Task.ContentHash, new string('b', 32), 1, credentialHash, "executor", Human, "turn", turnHash,
                policy, policyHash, credential, Task.UploadAdmissionDeadline);
            var binding = new DatasetImportExecutionBinding(Task.TaskId, 1, Task.ContentHash, 1, credentialHash, "executor", Human, "turn", turnHash, policy, policyHash, credential);
            Document = new()
            {
                WorkspaceId = "workspace",
                TargetId = "target",
                TenantId = "tenant",
                OwnerId = Human,
                AgentActorId = "executor",
                Members = [Human, "executor"],
                Turns = new() { ["turn"] = turn },
                DatasetImportTasks = new() { [Task.TaskId] = [Task] },
                DatasetImportExecutionBindings = new() { [Task.TaskId] = binding },
                DatasetImportPreparedAdmissions = new() { [Task.TaskId] = Prepared }
            };
            var lease = new JobExecutionLease { JobId = Prepared.OperationId, LeaseId = "lease", IssuedAt = now.AddDays(-1), ExpiresAt = now.AddHours(-1), ProtectedAuthority = "sealed-private-authority" };
            Job = new()
            {
                OperationId = Prepared.OperationId,
                Version = 1,
                Status = ExecutionJobStatus.Succeeded,
                CreatedAt = now.AddDays(-1),
                UpdatedAt = now,
                CompletedAt = now,
                AcceptanceDeadline = Prepared.AcceptBefore,
                Audit = new() { RequestedBy = "executor", TenantId = "tenant", SubmitterSecurityContext = policy with { ExecutionLease = lease } },
                Spec = new() { Kind = ExecutionJobKind.FileImport, TargetKind = BatchComputeTargetKind.KubernetesJob, Backend = "local", WorkloadName = "file-import" },
                FileImportOutput = new() { Schema = "actual_schema", PhysicalTableName = "actual_physical_table", FeatureCount = 3, DetectedSrid = 4326 }
            };
            CurrentJob = Job;
            Observation = new()
            {
                JobId = Job.OperationId,
                LeaseId = lease.LeaseId,
                IntentFingerprint = JobExecutionLeaseIntentFingerprint.Compute(Job),
                ExecutorActorId = "executor",
                InitiatingHumanActorId = Human,
                TenantId = "tenant",
                ExpiresAt = lease.ExpiresAt,
                Admission = new()
                {
                    WorkspaceId = "workspace",
                    TaskId = Task.TaskId,
                    TaskRevision = 1,
                    TaskContentHash = Task.ContentHash,
                    AuthorityRevision = 1,
                    AuthorityFingerprint = credentialHash,
                    PolicyContextFingerprint = policyHash,
                    InitiatingHumanActorId = Human,
                    OriginatingTurnId = "turn",
                    OriginalTurnHash = turnHash
                }
            };
            var store = Substitute.For<IAgentWorkspaceStore>();
            store.GetAsync("workspace").Returns(_ => Document); store.GetTargetIdAsync().Returns(_ => Target);
            var jobs = Substitute.For<IExecutionJobStore>(); jobs.GetAsync(Prepared.OperationId, Arg.Any<CancellationToken>()).Returns(_ => JobPresent ? CurrentJob : null);
            Leases.ResolveForObservationAsync(Job.OperationId, principal, JobExecutionLeaseObservationKind.Status, Arg.Any<CancellationToken>()).Returns(_ =>
                { Observed = principal; DuringObservation?.Invoke(); return Observation; });
            var tenant = Substitute.For<ITenantContext>(); tenant.TenantId.Returns("tenant");
            _services = new ServiceCollection().AddSingleton(store).AddSingleton(jobs).AddSingleton(Leases).AddSingleton(Tasks).AddSingleton(Contributors).AddSingleton(tenant).BuildServiceProvider();
            Context = new() { User = principal, RequestServices = _services }; Service = new(store);
            Reference = new("workspace", Task.TaskId, 1, Task.ContentHash);
        }
        public void Dispose() => _services.Dispose();
    }
}
