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
        }
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authority.RevalidateForExecutionAsync(fixture.Stored));
    }

    private sealed class Fixture : IDisposable
    {
        internal required ServiceProvider Services { get; init; }
        internal required ClaimsPrincipal Executor { get; init; }
        internal required ExecutionJobRecord Job { get; init; }
        internal required AgentWorkspaceDocument Document { get; set; }
        internal required CloudFile Metadata { get; set; }
        internal ExecutionJobRecord? Stored { get; set; }
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
                .Returns(_ => state?.Occupied ?? false);
            var sessions = Substitute.For<IDatabaseSessionFactory>();
            sessions.OpenAsync(Arg.Any<CancellationToken>()).Returns(session);
            var config = new ConfigurationBuilder().Build();
            var collection = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config)
                .AddSingleton<IAdminApiKeyStore>(keys).AddSingleton(workspaces).AddSingleton(jobs).AddSingleton(storage).AddSingleton(sessions)
                .AddSingleton<TimeProvider>(TimeProvider.System).AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
                .AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()))
                .AddSingleton<IOptions<OperatorApprovalOptions>>(Options.Create(new OperatorApprovalOptions()))
                .AddSingleton<IOperatorScopeAuthorizer, OperatorScopeAuthorizer>()
                .AddSingleton<IOperatorAuthorizationEvaluator, OperatorAuthorizationEvaluator>()
                .AddSingleton<IOperatorApprovalEvaluator, DefaultOperatorApprovalEvaluator>()
                .AddSingleton(Substitute.For<IOperationProposalStore>())
                .AddScoped<IJobExecutionLeasePolicy, ExecutionJobLeasePolicy>()
                .AddScoped<IJobExecutionLeaseScopeProvider, FileImportExecutionLeaseScopeProvider>()
                .AddScoped<JobExecutionLeaseAuthority>().AddSingleton<IJobExecutionLeaseAuthority, JobExecutionLeaseAuthorityFacade>();
            collection.AddApiKeyAuthentication(config);
            var services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var context = await AgentJobSecurityBridge.CaptureAsync(executor, new RbacOptions(), services, null, default);
            var owner = context.OwnerActorId!;
            var now = DateTimeOffset.UtcNow;
            var task = new DatasetImportTask("task", 1, "", owner, null, "target", "workspace", new("public", "roads"),
                new("browser-file", "roads.geojson", 100, new string('a', 64)), new(), now.AddHours(1));
            task = task with { ContentHash = DatasetImportTaskState.ContentHash(task) };
            var turn = new AgentWorkspaceTurn("turn", owner, "Import roads", new("workspace", "target", "view", 0, [], null), null);
            var turnHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(turn, AgentWorkspaceJsonContext.Default.AgentWorkspaceTurn)));
            var authorityHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(context.AgentCredential!, AgentWorkspaceJsonContext.Default.AgentCredentialSnapshot)));
            var policyHash = JobSecurityContextFingerprint.Compute(context);
            var prepared = new DatasetImportPreparedAdmission("task", 1, task.ContentHash, new string('c', 32), 1, authorityHash,
                owner, null, "turn", turnHash, context, policyHash, context.AgentCredential!, task.UploadAdmissionDeadline);
            var source = new ImportStagedSourceBinding("imports/resumable/" + new string('d', 32) + "/source-" + new string('e', 32), "generation", 100,
                new string('a', 64), Transfer: new(new string('d', 32), owner, null, "target", "workspace", "task", 1, task.ContentHash));
            var intent = DatasetImportSubmissionIntent.Create(task, prepared, source);
            var document = new AgentWorkspaceDocument { WorkspaceId = "workspace", TargetId = "target", OwnerId = owner, Members = [owner] };
            document.DatasetImportTasks[task.TaskId] = [task];
            document.DatasetImportPreparedAdmissions[task.TaskId] = prepared;
            document.DatasetImportExecutionBindings[task.TaskId] = new("task", 1, task.ContentHash, 1, authorityHash, owner,
                null, "turn", turnHash, context, policyHash, context.AgentCredential!);
            document.Turns[turn.TurnId] = turn;
            var job = new ExecutionJobRecord
            {
                OperationId = prepared.OperationId, Status = ExecutionJobStatus.Queued, CreatedAt = now, UpdatedAt = now,
                AcceptanceDeadline = prepared.AcceptBefore,
                Audit = new() { RequestedBy = owner, OperationInstanceId = prepared.OperationId, SubmitterSecurityContext = context },
                Spec = new() { Kind = ExecutionJobKind.FileImport, TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = "local", WorkloadName = "file-import", Parameters = FileImportJobParameters.Build(intent.Request, source.Bytes) }
            };
            state = new Fixture { Services = services, Executor = executor, Job = job, Document = document,
                Metadata = new() { FileId = source.FileId, FileName = "roads.geojson", StoragePath = source.FileId,
                    ContentType = "application/geo+json", SizeBytes = 100, UploadedAt = now, ETag = source.ETag, Provider = CloudStorageProvider.Local } };
            return state;
        }
        public void Dispose() => Services.Dispose();
    }
}
