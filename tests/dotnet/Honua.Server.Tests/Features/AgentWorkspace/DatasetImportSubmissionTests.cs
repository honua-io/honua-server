// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Import.FileImport;
using Honua.Server.Features.AgentWorkspace;
using Honua.Server.Features.Import;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.AgentWorkspace;

// These tests exercise queue composition. The lease policy is a controlled port;
// live credential/resource/approval qualification belongs to its integration tests.
public sealed class DatasetImportSubmissionTests
{
    [Fact]
    public async Task Submission_SealsOriginalAuthorityBeforeAcceptanceAndReplaysWithoutNewLeaseOrDispatch()
    {
        var fixture = Intent();
        using var queue = new Harness(fixture.Task, fixture.Prepared);
        var caller = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "human")], "test"));
        var executor = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "agent")], "test"));
        var id = await queue.Service.QueueDatasetImportAsync(fixture.Task, fixture.Prepared, fixture.Source, caller, executor);
        Assert.Equal(fixture.Prepared.OperationId, id);
        Assert.Equal(new[] { "issue", "create", "dispatch" }, queue.Events);
        Assert.Same(executor, queue.Issued!.Executor);
        Assert.Equal(fixture.Source.ExpiresAt, queue.Issued.RequestedExpiresAt);
        Assert.Equal(fixture.Task.OwnerActorId, queue.Stored!.Audit.RequestedBy);
        Assert.Equal(fixture.Prepared.ExecutorPolicyContext, queue.Issued.PreparedJob.Audit.SubmitterSecurityContext);
        Assert.NotNull(queue.Stored.Audit.SubmitterSecurityContext!.ExecutionLease);
        Assert.Equal("human", queue.Issued.Admission!.InitiatingHumanActorId);
        Assert.Equal(fixture.Task.UploadAdmissionDeadline, queue.Stored.AcceptanceDeadline);

        Assert.Equal(id, await queue.Service.QueueDatasetImportAsync(fixture.Task, fixture.Prepared, fixture.Source, caller, executor));
        Assert.Equal(new[] { "issue", "create", "dispatch", "observe" }, queue.Events);
        Assert.Same(caller, queue.Observer);
    }

    [Fact]
    public async Task Submission_PolicyRefusalCannotCreateOrDispatchJob()
    {
        var fixture = Intent();
        using var queue = new Harness(fixture.Task, fixture.Prepared);
        queue.RejectIssuance = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queue.Service.QueueDatasetImportAsync(
            fixture.Task, fixture.Prepared, fixture.Source, new ClaimsPrincipal(), new ClaimsPrincipal()));
        Assert.Null(queue.Stored);
        Assert.Empty(queue.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submission_RequiresActualCallerBeforeAndAfterLeaseIssuance(bool revokeDuringIssuance)
    {
        var fixture = Intent();
        using var queue = new Harness(fixture.Task, fixture.Prepared)
        { CallerAllowed = revokeDuringIssuance, RevokeCallerDuringIssuance = revokeDuringIssuance };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queue.Service.QueueDatasetImportAsync(
            fixture.Task, fixture.Prepared, fixture.Source, new ClaimsPrincipal(), new ClaimsPrincipal()));
        Assert.Null(queue.Stored);
        Assert.Equal(revokeDuringIssuance ? new[] { "issue" } : [], queue.Events);
    }

    [Fact]
    public async Task GenericImportEntry_CannotDowngradeTaskBoundTransferAuthority()
    {
        var fixture = Intent();
        using var queue = new Harness(fixture.Task, fixture.Prepared);
        var intent = DatasetImportSubmissionIntent.Create(fixture.Task, fixture.Prepared, fixture.Source);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queue.Service.QueueImportAsync(intent.Request, fixture.Source.Bytes));
        Assert.Null(queue.Stored);
        Assert.Empty(queue.Events);
    }

    [Fact]
    public async Task Replay_RequiresActualAcceptedAuthorityAndDoesNotIssueReplacement()
    {
        var fixture = Intent();
        using var queue = new Harness(fixture.Task, fixture.Prepared);
        await queue.Service.QueueDatasetImportAsync(fixture.Task, fixture.Prepared, fixture.Source,
            new ClaimsPrincipal(), new ClaimsPrincipal());
        queue.RejectObservation = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queue.Service.QueueDatasetImportAsync(
            fixture.Task, fixture.Prepared, fixture.Source, new ClaimsPrincipal(), new ClaimsPrincipal()));
        Assert.Equal(new[] { "issue", "create", "dispatch" }, queue.Events);
    }

    [Fact]
    public async Task LostAcceptanceReply_RecoversOriginalLeaseWithoutRedispatch()
    {
        var fixture = Intent();
        using var queue = new Harness(fixture.Task, fixture.Prepared) { LoseCreateReply = true };
        await Assert.ThrowsAsync<IOException>(() => queue.Service.QueueDatasetImportAsync(
            fixture.Task, fixture.Prepared, fixture.Source, new ClaimsPrincipal(), new ClaimsPrincipal()));
        var original = queue.Stored;
        Assert.NotNull(original);
        Assert.Equal(fixture.Prepared.OperationId, await queue.Service.QueueDatasetImportAsync(
            fixture.Task, fixture.Prepared, fixture.Source, new ClaimsPrincipal(), new ClaimsPrincipal()));
        Assert.Same(original, queue.Stored);
        Assert.Equal(new[] { "issue", "create", "observe" }, queue.Events);
    }

    [Fact]
    public async Task Replay_RejectsDifferentPreparedAuthorityEvenWhenFileParametersMatch()
    {
        var fixture = Intent();
        using var queue = new Harness(fixture.Task, fixture.Prepared);
        await queue.Service.QueueDatasetImportAsync(fixture.Task, fixture.Prepared, fixture.Source,
            new ClaimsPrincipal(), new ClaimsPrincipal());
        queue.WrongObservedAdmission = true;
        await Assert.ThrowsAsync<ImportAcceptanceConflictException>(() => queue.Service.QueueDatasetImportAsync(
            fixture.Task, fixture.Prepared, fixture.Source, new ClaimsPrincipal(), new ClaimsPrincipal()));
        Assert.Equal(new[] { "issue", "create", "dispatch", "observe" }, queue.Events);
    }

    [Theory]
    [InlineData("task-content")]
    [InlineData("task-revision")]
    [InlineData("owner")]
    [InlineData("deadline")]
    [InlineData("policy")]
    [InlineData("credential")]
    [InlineData("authority")]
    [InlineData("transfer-target")]
    [InlineData("transfer-workspace")]
    [InlineData("source-bytes")]
    [InlineData("source-digest")]
    public void Projection_RejectsMixedRetainedInputs(string change)
    {
        var (task, prepared, source) = Intent();
        switch (change)
        {
            case "task-content": task = task with { Destination = new("public", "other") }; break;
            case "task-revision": prepared = prepared with { TaskRevision = prepared.TaskRevision + 1 }; break;
            case "owner": prepared = prepared with { ExecutorActorId = "other" }; break;
            case "deadline": prepared = prepared with { AcceptBefore = prepared.AcceptBefore.AddMinutes(1) }; break;
            case "policy": prepared = prepared with { ExecutorPolicyContext = prepared.ExecutorPolicyContext with { Claims = [new("department", "other")] } }; break;
            case "credential": prepared = prepared with { ExecutorAuthority = prepared.ExecutorAuthority with { TokenExpiresAt = prepared.ExecutorAuthority.TokenExpiresAt.AddHours(1) } }; break;
            case "authority": prepared = prepared with { AuthorityFingerprint = new string('c', 64) }; break;
            case "transfer-target": source = source with { Transfer = source.Transfer! with { TargetId = "other" } }; break;
            case "transfer-workspace": source = source with { Transfer = source.Transfer! with { WorkspaceId = "other" } }; break;
            case "source-bytes": source = source with { Bytes = source.Bytes + 1 }; break;
            case "source-digest": source = source with { Sha256 = new string('d', 64) }; break;
        }
        Assert.Throws<InvalidOperationException>(() => DatasetImportSubmissionIntent.Create(task, prepared, source));
    }

    [Theory]
    [InlineData(ImportLoadMode.Append)]
    [InlineData(ImportLoadMode.Replace)]
    public void Projection_PreservesExplicitLoadBehaviorAnd64BitSize(ImportLoadMode mode)
    {
        var fixture = Intent(mode);
        var intent = DatasetImportSubmissionIntent.Create(fixture.Task, fixture.Prepared, fixture.Source);
        Assert.Equal(mode, intent.Request.EffectiveLoadMode);
        Assert.Equal(5L * 1024 * 1024 * 1024, intent.Request.StagedSource!.Bytes);
        Assert.Null(intent.Request.FileStream);
        Assert.Null(intent.Request.LocalFilePath);
        Assert.Equal(fixture.Source.FileId, intent.Request.CloudFileId);
    }

    private static (DatasetImportTask Task, DatasetImportPreparedAdmission Prepared, ImportStagedSourceBinding Source)
        Intent(ImportLoadMode mode = ImportLoadMode.Append)
    {
        var now = DateTimeOffset.UtcNow;
        var task = new DatasetImportTask("task-a", 1, "", "agent", "tenant", "docker-a", "workspace-a",
            new("public", "parcels"), new("browser-file", "parcels.geojson", 5L * 1024 * 1024 * 1024, new string('a', 64)),
            new(mode, 3857, 4326), now.AddHours(1));
        task = task with { ContentHash = DatasetImportTaskState.ContentHash(task) };
        var credential = new AgentCredentialSnapshot { Kind = "api-key", Id = Guid.NewGuid().ToString(), Permissions = ["agent:workspace"] };
        var context = new JobSecurityContext("agent", "tenant", [new("department", "gis")])
        { OwnerActorId = "agent", WorkspaceOwnerId = "human", AgentCredential = credential };
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            credential, AgentWorkspaceJsonContext.Default.AgentCredentialSnapshot)));
        var prepared = new DatasetImportPreparedAdmission(task.TaskId, task.Revision, task.ContentHash, Guid.NewGuid().ToString("N"),
            1, fingerprint, task.OwnerActorId, "human", "turn-a", new string('b', 64), context,
            JobSecurityContextFingerprint.Compute(context), credential, task.UploadAdmissionDeadline);
        var uploadId = Guid.NewGuid().ToString("N");
        var transfer = new ImportTransferProvenance(uploadId, task.OwnerActorId, task.TenantId, task.TargetId,
            task.WorkspaceId, task.TaskId, task.Revision, task.ContentHash);
        var source = new ImportStagedSourceBinding($"imports/resumable/{uploadId}/source-{Guid.NewGuid():N}", "generation-1",
            task.Source.TotalBytes, task.Source.ExpectedSha256!, now.AddHours(2), transfer);
        return (task, prepared, source);
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;
        internal IJobExecutionLeaseAuthority Authority { get; } = Substitute.For<IJobExecutionLeaseAuthority>();
        internal List<string> Events { get; } = [];
        internal ExecutionJobRecord? Stored { get; private set; }
        internal JobExecutionLeaseIssuanceRequest? Issued { get; private set; }
        internal ClaimsPrincipal? Observer { get; private set; }
        internal bool CallerAllowed { get; set; } = true;
        internal bool RevokeCallerDuringIssuance { get; set; }
        internal bool RejectIssuance { get; set; }
        internal bool RejectObservation { get; set; }
        internal bool LoseCreateReply { get; set; }
        internal bool WrongObservedAdmission { get; set; }
        internal DurableImportJobService Service { get; }

        internal Harness(DatasetImportTask task, DatasetImportPreparedAdmission prepared)
        {
            var importer = Substitute.For<IFileImportService>();
            importer.DetectFormat(Arg.Any<string>()).Returns(SupportedFileFormat.GeoJson);
            importer.Limits.Returns(new ImportLimits());
            var tasks = Substitute.For<IDatasetImportTaskProvider>();
            tasks.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(),
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(_ => System.Threading.Tasks.Task.FromResult<DatasetImportTask?>(CallerAllowed ? task : null));
            var admissions = Substitute.For<IDatasetImportAdmissionProvider>();
            admissions.ReadAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(System.Threading.Tasks.Task.FromResult<DatasetImportPreparedAdmission?>(prepared));
            _services = new ServiceCollection().AddSingleton(importer).AddSingleton(Authority).AddSingleton(tasks)
                .AddSingleton(admissions).BuildServiceProvider();
            var store = Substitute.For<IExecutionJobStore>();
            store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Stored));
            store.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<ExecutionJobRecord>>([]));
            store.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Events.Add("create");
                Stored = call.ArgAt<ExecutionJobRecord>(0) with { Version = 1 };
                if (LoseCreateReply) return Task.FromException<bool>(new IOException("Acceptance reply lost"));
                return Task.FromResult(true);
            });
            var queue = Substitute.For<IJobQueue>();
            queue.EnqueueAsync(Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>()).Returns(_ =>
            { Events.Add("dispatch"); return Task.CompletedTask; });
            Authority.IssueAsync(Arg.Any<JobExecutionLeaseIssuanceRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                if (RejectIssuance) return Task.FromException<JobExecutionLease>(new UnauthorizedAccessException("Policy refused"));
                Events.Add("issue");
                if (RevokeCallerDuringIssuance) CallerAllowed = false;
                Issued = call.ArgAt<JobExecutionLeaseIssuanceRequest>(0);
                return Task.FromResult(new JobExecutionLease
                {
                    LeaseId = "lease-a",
                    JobId = Issued.PreparedJob.OperationId,
                    IssuedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = Issued.RequestedExpiresAt,
                    ProtectedAuthority = "test-only"
                });
            });
            Authority.ResolveForObservationAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(),
                Arg.Any<JobExecutionLeaseObservationKind>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                if (RejectObservation) return Task.FromException<JobExecutionLeaseObservation>(new UnauthorizedAccessException("Revoked"));
                Events.Add("observe"); Observer = call.ArgAt<ClaimsPrincipal>(1);
                return Task.FromResult(new JobExecutionLeaseObservation
                {
                    JobId = Stored!.OperationId,
                    LeaseId = "lease-a",
                    IntentFingerprint = JobExecutionLeaseIntentFingerprint.Compute(Stored),
                    ExecutorActorId = "agent",
                    TenantId = "tenant",
                    ExpiresAt = Issued!.RequestedExpiresAt,
                    Admission = WrongObservedAdmission ? Issued.Admission! with { AuthorityRevision = Issued.Admission!.AuthorityRevision + 1 } : Issued.Admission
                });
            });
            Service = new DurableImportJobService(Substitute.For<IImportJobService>(),
                BackgroundImportRunnerDecision.Decide(true, null, frozenBetweenRequests: false),
                Substitute.For<IUniversalProgressStore>(), _services.GetRequiredService<IServiceScopeFactory>(), [],
                NullLogger<DurableImportJobService>.Instance, store, queue);
        }

        public void Dispose() { Service.Dispose(); _services.Dispose(); }
    }
}
