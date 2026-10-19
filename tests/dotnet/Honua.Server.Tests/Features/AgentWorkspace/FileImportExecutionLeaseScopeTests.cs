// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Import.FileImport.Durable;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

// Actual Host facade, canonical named admin/operator/approval policies and stored key;
// metadata/session ports are controlled. No database mutation or byte-transfer claim.
public sealed class FileImportExecutionLeaseScopeTests
{
    [UnitTheory]
    [InlineData("unchanged")]
    [InlineData("workspace")]
    [InlineData("occupied")]
    [InlineData("source-generation")]
    [InlineData("revoked-during-source")]
    [InlineData("different-request")]
    [InlineData("browser-selection")]
    [InlineData("agent-link")]
    [InlineData("source-expired")]
    public async Task AcceptedTask_UsesCurrentSourceDestinationAndActualJobScopeWithoutHttpContext(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        var authority = fixture.Services.GetRequiredService<IJobExecutionLeaseAuthority>();
        var lease = await authority.IssueAsync(new() { PreparedJob = fixture.Job, Executor = fixture.Executor,
            RequestedExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20) });
        fixture.Stored = fixture.Job with { Audit = fixture.Job.Audit with
            { SubmitterSecurityContext = fixture.Job.Audit.SubmitterSecurityContext! with { ExecutionLease = lease } } };
        switch (change)
        {
            case "workspace": fixture.Document = fixture.Document with { Members = [] }; break;
            case "browser-selection": fixture.Document = fixture.Document with { Turns = new(fixture.Document.Turns) { ["turn"] = fixture.Document.Turns["turn"] with { BrowserFileSelection = new("other", "roads.geojson", 100, new string('a', 64)) } } }; break;
            case "agent-link": fixture.Document = fixture.Document with { AgentActorId = "other" }; break;
            case "source-expired": fixture.Clock.Now = fixture.Clock.Now.AddHours(1); break;
            case "occupied": fixture.Occupied = true; break;
            case "source-generation": fixture.Metadata = fixture.Metadata with { ETag = "replacement" }; break;
            case "revoked-during-source": fixture.RevokeOnMetadata = true; break;
            case "different-request": fixture.Stored = fixture.Stored with { Spec = fixture.Stored.Spec with
                { Parameters = new Dictionary<string, string>(fixture.Stored.Spec.Parameters) { [FileImportJobParameters.TableName] = "other" } } }; break;
        }
        if (change == "unchanged")
        {
            var current = await authority.RevalidateForExecutionAsync(fixture.Stored);
            using var scope = JobSecurityScope.BeginExecution(fixture.Stored, current);
            Assert.True(await ImportAdminAuthorization.IsAuthorizedAsync(fixture.Services, current.CurrentAgentPrincipal!, "target", true, default));
            Assert.False(current.CurrentAgentPrincipal!.IsInRole("admin"));
            Assert.Equal("imported_roads", fixture.CheckedPhysicalTable);
        }
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authority.RevalidateForExecutionAsync(fixture.Stored));
    }

    [UnitTheory]
    [InlineData("unchanged")]
    [InlineData("no-scope")]
    [InlineData("schema")]
    [InlineData("physical-table")]
    [InlineData("request")]
    [InlineData("source")]
    [InlineData("cancelled")]
    public async Task CommitBoundary_PinsActualScopePhysicalDestinationAndMaterializedRequest(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        var leases = fixture.Services.GetRequiredService<IJobExecutionLeaseAuthority>();
        var lease = await leases.IssueAsync(new() { PreparedJob = fixture.Job, Executor = fixture.Executor,
            RequestedExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20) });
        fixture.Stored = fixture.Job with { Audit = fixture.Job.Audit with
            { SubmitterSecurityContext = fixture.Job.Audit.SubmitterSecurityContext! with { ExecutionLease = lease } } };
        var current = await leases.RevalidateForExecutionAsync(fixture.Stored);
        Assert.True(FileImportJobParameters.TryParse(fixture.Stored.Spec.Parameters, out var request, out _, out _));
        using var bytes = new MemoryStream(new byte[100]);
        request = request! with { CloudFileId = null, FileStream = bytes };
        var schema = "public";
        var table = "imported_roads";
        if (change == "schema") schema = "other";
        if (change == "physical-table") table = "roads";
        if (change == "request") request = request with { TargetSrid = 3857 };
        if (change == "source") request = request with { StagedSource = request.StagedSource! with { ETag = "other" } };
        if (change == "cancelled") fixture.Stored = fixture.Stored with { CancellationRequestedAt = DateTimeOffset.UtcNow };
        using var scope = change == "no-scope" ? null : JobSecurityScope.BeginExecution(fixture.Stored, current);
        var authority = new FileImportExecutionCommitAuthority(fixture.Services.GetRequiredService<IExecutionJobStore>(), leases);
        if (change == "unchanged")
        {
            var binding = await authority.RequireCurrentAsync(request, schema, table);
            Assert.Equal(fixture.Stored.OperationId, binding.OperationId);
            Assert.Equal(JobExecutionLeaseIntentFingerprint.Compute(fixture.Stored), binding.IntentFingerprint);
            Assert.True(binding.RequireAbsentTarget);
        }
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authority.RequireCurrentAsync(request, schema, table));
    }

    [UnitTheory]
    [InlineData("unchanged")]
    [InlineData("executor-revoked")]
    [InlineData("human-revoked")]
    [InlineData("workspace-removed")]
    [InlineData("source-selection-changed")]
    [InlineData("output-substituted")]
    public async Task TerminalHumanObservation_UsesCurrentHumanRightsWithoutRestoringExecutor(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        var authority = fixture.Services.GetRequiredService<IJobExecutionLeaseAuthority>();
        var prepared = fixture.Document.DatasetImportPreparedAdmissions["task"];
        var lease = await authority.IssueAsync(new() { PreparedJob = fixture.Job, Executor = fixture.Executor,
            RequestedExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20), Admission = new()
            {
                WorkspaceId = "workspace", TaskId = "task", TaskRevision = 1,
                TaskContentHash = fixture.Document.DatasetImportTasks["task"][0].ContentHash,
                AuthorityRevision = prepared.AuthorityRevision, AuthorityFingerprint = prepared.AuthorityFingerprint,
                PolicyContextFingerprint = prepared.PolicyContextFingerprint, InitiatingHumanActorId = prepared.InitiatingHumanActorId,
                OriginatingTurnId = prepared.OriginatingTurnId, OriginalTurnHash = prepared.OriginalTurnHash
            } });
        fixture.Stored = fixture.Job with { Status = ExecutionJobStatus.Succeeded, Audit = fixture.Job.Audit with
            { SubmitterSecurityContext = fixture.Job.Audit.SubmitterSecurityContext! with { ExecutionLease = lease } },
            FileImportOutput = new() { Schema = "public", PhysicalTableName = "imported_roads", FeatureCount = 17 } };
        fixture.Stored = fixture.Stored with { CommittedEffectReferences =
            [$"honua-import-commit:{fixture.Stored.OperationId}:{JobExecutionLeaseIntentFingerprint.Compute(fixture.Stored)}"] };
        fixture.Clock.Now = lease.ExpiresAt.AddHours(1);
        fixture.Occupied = true;
        if (change == "executor-revoked") await fixture.Keys.RevokeAsync(fixture.ExecutorKeyId, default);
        if (change == "human-revoked") await fixture.Keys.RevokeAsync(Guid.Parse(fixture.Human.FindFirst("api_key_id")!.Value), default);
        if (change == "workspace-removed") fixture.Document = fixture.Document with { Members = [] };
        if (change == "source-selection-changed") fixture.Document.Turns["turn"] = fixture.Document.Turns["turn"] with
            { BrowserFileSelection = new("other", "roads.geojson", 100, new string('a', 64)) };
        if (change == "output-substituted") fixture.Stored = fixture.Stored with { FileImportOutput = fixture.Stored.FileImportOutput! with { PhysicalTableName = "other" } };
        if (change is "unchanged" or "executor-revoked")
        {
            var observed = await authority.ResolveForObservationAsync(fixture.Stored.OperationId, fixture.Human, JobExecutionLeaseObservationKind.OutputContent);
            Assert.Equal(lease.LeaseId, observed.LeaseId);
            Assert.Null(JobSecurityScope.Current);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authority.RevalidateForExecutionAsync(fixture.Stored));
        }
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authority.ResolveForObservationAsync(fixture.Stored.OperationId,
            fixture.Human, JobExecutionLeaseObservationKind.OutputContent));
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        internal required ServiceProvider Services { get; init; }
        internal required ClaimsPrincipal Executor { get; init; }
        internal required ClaimsPrincipal Human { get; init; }
        internal required InMemoryAdminApiKeyStore Keys { get; init; }
        internal required Guid ExecutorKeyId { get; init; }
        internal required ExecutionJobRecord Job { get; init; }
        internal required AgentWorkspaceDocument Document { get; set; }
        internal required CloudFile Metadata { get; set; }
        internal ExecutionJobRecord? Stored { get; set; }
        internal required TestClock Clock { get; init; }
        internal string? CheckedPhysicalTable { get; set; }
        internal bool Occupied { get; set; }
        internal bool RevokeOnMetadata { get; set; }

        internal static async Task<Fixture> CreateAsync()
        {
            var keys = new InMemoryAdminApiKeyStore();
            var key = await keys.CreateAsync("executor", ["agent:workspace", "admin:write"], null, null, default);
            var executor = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(key.Record);
            Fixture? state = null;
            var workspaces = Substitute.For<IAgentWorkspaceStore>();
            workspaces.GetTargetIdAsync().Returns("target");
            workspaces.GetAsync("workspace").Returns(_ => Task.FromResult<AgentWorkspaceDocument?>(state?.Document));
            var jobs = Substitute.For<IExecutionJobStore>();
            jobs.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(state?.Stored));
            var storage = Substitute.For<ICloudFileStorage>();
            storage.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async _ =>
            {
                if (state!.RevokeOnMetadata) await keys.RevokeAsync(key.Record.Id, default);
                return state.Metadata;
            });
            var session = Substitute.For<IDatabaseSession>();
            session.QuerySingleOrDefaultAsync<bool>(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    if (state is not null) state.CheckedPhysicalTable = (string)((Dictionary<string, object?>)call[1])["table"]!;
                    return state?.Occupied ?? false;
                });
            var sessions = Substitute.For<IDatabaseSessionFactory>();
            sessions.OpenAsync(Arg.Any<CancellationToken>()).Returns(session);
            var clock = new TestClock { Now = DateTimeOffset.UtcNow };
            var config = new ConfigurationBuilder().Build();
            var collection = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config)
                .AddSingleton<IAdminApiKeyStore>(keys).AddSingleton(workspaces).AddSingleton(jobs).AddSingleton(storage).AddSingleton(sessions)
                .AddSingleton<TimeProvider>(clock).AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
                .AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()))
                .AddSingleton<IOptions<OperatorApprovalOptions>>(Options.Create(new OperatorApprovalOptions()))
                .AddSingleton<IOperatorScopeAuthorizer, OperatorScopeAuthorizer>()
                .AddSingleton<IOperatorAuthorizationEvaluator, OperatorAuthorizationEvaluator>()
                .AddSingleton<IOperatorApprovalEvaluator, DefaultOperatorApprovalEvaluator>()
                .AddSingleton(Substitute.For<IOperationProposalStore>())
                .AddScoped<IJobExecutionLeaseObservationPolicy>(provider => (ExecutionJobLeasePolicy)provider.GetRequiredService<IJobExecutionLeasePolicy>())
                .AddScoped<IJobExecutionLeasePolicy, ExecutionJobLeasePolicy>()
                .AddScoped<IJobExecutionLeaseScopeProvider, FileImportExecutionLeaseScopeProvider>()
                .AddScoped<JobExecutionLeaseAuthority>().AddSingleton<IJobExecutionLeaseAuthority, JobExecutionLeaseAuthorityFacade>();
            collection.AddApiKeyAuthentication(config);
            var services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var context = await AgentJobSecurityBridge.CaptureAsync(executor, new RbacOptions(), services, null, default);
            var owner = context.OwnerActorId!;
            var humanKey = await keys.CreateAsync("human", ["*"], null, null, default);
            var humanPrincipal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(humanKey.Record);
            var human = CanonicalSecurityActor.Resolve(humanPrincipal)!.ActorId;
            var now = DateTimeOffset.UtcNow;
            var task = new DatasetImportTask("task", 1, "", owner, null, "target", "workspace", new("public", "roads"),
                new("browser-file", "roads.geojson", 100, new string('a', 64), BrowserSelectionId: "selection"), new(), now.AddHours(1));
            task = task with { ContentHash = DatasetImportTaskState.ContentHash(task) };
            var turn = new AgentWorkspaceTurn("turn", human, "Import roads", new("workspace", "target", "view", 0, [], null), null, BrowserFileSelection: new("selection", "roads.geojson", 100, new string('a', 64)));
            var turnHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(turn, AgentWorkspaceJsonContext.Default.AgentWorkspaceTurn)));
            var authorityHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(context.AgentCredential!, AgentWorkspaceJsonContext.Default.AgentCredentialSnapshot)));
            var policyHash = JobSecurityContextFingerprint.Compute(context);
            var prepared = new DatasetImportPreparedAdmission("task", 1, task.ContentHash, new string('c', 32), 1, authorityHash,
                owner, human, "turn", turnHash, context, policyHash, context.AgentCredential!, task.UploadAdmissionDeadline);
            var source = new ImportStagedSourceBinding("imports/resumable/" + new string('d', 32) + "/source-" + new string('e', 32), "generation", 100,
                new string('a', 64), ExpiresAt: clock.Now.AddMinutes(30), Transfer: new(new string('d', 32), owner, null, "target", "workspace", "task", 1, task.ContentHash));
            var intent = DatasetImportSubmissionIntent.Create(task, prepared, source);
            var document = new AgentWorkspaceDocument { WorkspaceId = "workspace", TargetId = "target", OwnerId = human, AgentActorId = owner, Members = [human, owner] };
            document.DatasetImportTasks[task.TaskId] = [task];
            document.DatasetImportPreparedAdmissions[task.TaskId] = prepared;
            document.DatasetImportExecutionBindings[task.TaskId] = new("task", 1, task.ContentHash, 1, authorityHash, owner,
                human, "turn", turnHash, context, policyHash, context.AgentCredential!);
            document.Turns[turn.TurnId] = turn;
            var job = new ExecutionJobRecord
            {
                OperationId = prepared.OperationId, Status = ExecutionJobStatus.Queued, CreatedAt = now, UpdatedAt = now,
                AcceptanceDeadline = prepared.AcceptBefore,
                Audit = new() { RequestedBy = owner, OperationInstanceId = prepared.OperationId, SubmitterSecurityContext = context },
                Spec = new() { Kind = ExecutionJobKind.FileImport, TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = "local", WorkloadName = "file-import", Parameters = FileImportJobParameters.Build(intent.Request, source.Bytes) }
            };
            state = new Fixture { Services = services, Executor = executor, Human = humanPrincipal, Keys = keys, ExecutorKeyId = key.Record.Id, Job = job, Document = document,
                Clock = clock, Metadata = new() { FileId = source.FileId, FileName = "roads.geojson", StoragePath = source.FileId,
                    ContentType = "application/geo+json", SizeBytes = 100, UploadedAt = now, ETag = source.ETag, ExpiresAt = source.ExpiresAt, Provider = CloudStorageProvider.Local } };
            return state;
        }
        public void Dispose() => Services.Dispose();
    }
}
