// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.AgentWorkspace.Domain;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

// Controlled canonical capture/task/turn ports exercise renewal orchestration.
// This is not OIDC issuer, refreshed-token authentication, or accepted-job lease proof.
public sealed class DatasetImportAuthorityRenewalTests
{
    [UnitTheory]
    [InlineData("expiry-only")]
    [InlineData("department-change")]
    [InlineData("key-rotation")]
    [InlineData("profile-generation")]
    [InlineData("write-revoked")]
    [InlineData("preparation-race")]
    [InlineData("postcommit-membership-revoked")]
    public async Task RenewalKeepsOriginalIntentPolicyAndDeadlineAndCannotOverridePreparation(string scenario)
    {
        var keys = new InMemoryAdminApiKeyStore();
        var issued = await keys.CreateAsync("renewal-agent", ["agent:workspace", "admin:read"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(issued.Record);
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        var tasks = Substitute.For<IDatasetImportTaskProvider>();
        var turns = Substitute.For<IAgentWorkspaceTurnAuthorizer>();
        var capture = Substitute.For<IAgentJobSecurityContextCapture>();
        using var services = new ServiceCollection().AddSingleton<IAdminApiKeyStore>(keys).AddSingleton(tenant)
            .AddSingleton(tasks).AddSingleton(turns).AddSingleton(capture).AddSingleton<TimeProvider>(TimeProvider.System).BuildServiceProvider();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        context.Request.Headers["X-Honua-Workspace"] = "workspace-1";
        context.Request.Headers["X-Honua-Turn"] = "turn-1";
        var originalAuthority = (await AgentCredentialAuthority.CaptureAsync(principal, services, default))!
            with
        { TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) };
        var originalPolicy = new JobSecurityContext(actor, "tenant-1", [new("department", "north"), new("exp", "original-exp")], "role")
        { OwnerActorId = actor, AgentCredential = originalAuthority };
        var task = new DatasetImportTask("import-task-1", 1, string.Empty, actor, "tenant-1", "target-1", "workspace-1",
            new("public", "new_table"), new("browser-file", "large.geojson", 5L * 1024 * 1024 * 1024, new string('a', 64)),
            new(), DateTimeOffset.UtcNow.AddHours(24));
        task = task with { ContentHash = DatasetImportTaskState.ContentHash(task) };
        var turn = new AgentWorkspaceTurn("turn-1", "human-1", "Import selected file",
            new("workspace-1", "target-1", "view-1", 0, [], null, "tenant-1"), null);
        var binding = new DatasetImportExecutionBinding(task.TaskId, 1, task.ContentHash, 1, "original-authority-hash", actor,
            "human-1", turn.TurnId,
            Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(turn, AgentWorkspaceJsonContext.Default.AgentWorkspaceTurn))),
            originalPolicy, JobSecurityContextFingerprint.Compute(originalPolicy), originalAuthority);
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = "workspace-1",
            TargetId = "target-1",
            OwnerId = "human-1",
            AgentActorId = actor,
            TenantId = "tenant-1",
            Members = [actor, "human-1"],
            Turns = new() { [turn.TurnId] = turn },
            DatasetImportTasks = new() { [task.TaskId] = [task] },
            DatasetImportExecutionBindings = new() { [task.TaskId] = binding },
        };
        var store = Substitute.For<IAgentWorkspaceStore>();
        store.GetAsync("workspace-1").Returns(_ => document);
        store.GetTargetIdAsync().Returns("target-1");
        store.SaveAsync(Arg.Any<AgentWorkspaceDocument>()).Returns(call =>
        {
            var submitted = call.Arg<AgentWorkspaceDocument>();
            if (submitted.Version != document.Version) return false;
            document = submitted with { Version = submitted.Version + 1 };
            if (scenario == "postcommit-membership-revoked") document = document with { Members = ["human-1"] };
            return true;
        });
        tasks.AuthorizeAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => scenario == "write-revoked" && call.ArgAt<bool>(5) ? null : task);
        turns.AuthorizeAsync(principal, "workspace-1", turn.TurnId, Arg.Any<CancellationToken>())
            .Returns(new AgentWorkspaceTurnAuthorization("workspace-1", turn.TurnId, "target-1", "tenant-1", "test-release", "human-1"));
        var freshAuthority = originalAuthority with { TokenExpiresAt = originalAuthority.TokenExpiresAt.AddHours(1) };
        if (scenario == "key-rotation") freshAuthority = freshAuthority with { CredentialVersion = "rotated" };
        if (scenario == "profile-generation") freshAuthority = freshAuthority with { Generation = originalAuthority.Generation + 1 };
        var freshPolicy = originalPolicy with
        {
            Claims = [new("department", scenario == "department-change" ? "south" : "north"), new("exp", "refreshed-exp")],
            AgentCredential = freshAuthority,
        };
        capture.CaptureCurrentAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (scenario == "preparation-race")
            {
                var prepared = new DatasetImportPreparedAdmission(task.TaskId, 1, task.ContentHash, new string('b', 32), 1,
                    binding.AuthorityFingerprint, actor, "human-1", turn.TurnId, binding.OriginalTurnHash,
                    originalPolicy, binding.PolicyContextFingerprint, originalAuthority, task.UploadAdmissionDeadline);
                document = document with
                {
                    Version = document.Version + 1,
                    DatasetImportPreparedAdmissions = new() { [task.TaskId] = prepared }
                };
            }
            return freshPolicy;
        });
        var request = new RenewDatasetImportAuthority("renewal-1", 1, task.ContentHash);
        if (scenario == "preparation-race")
        {
            await FluentActions.Awaiting(async () => await DatasetImportAuthorityRenewalService.RenewAsync(context, store,
                "workspace-1", task.TaskId, 1, request)).Should().ThrowAsync<AgentWorkspaceConflictException>();
            document.DatasetImportPreparedAdmissions.Should().ContainSingle();
            document.DatasetImportExecutionBindings[task.TaskId].AuthorityRevision.Should().Be(1);
        }
        else
        {
            var receipt = await DatasetImportAuthorityRenewalService.RenewAsync(context, store, "workspace-1", task.TaskId, 1, request);
            if (scenario == "expiry-only")
            {
                receipt!.AuthorityRevision.Should().Be(2);
                var retained = document.DatasetImportExecutionBindings[task.TaskId];
                retained.ExecutorPolicyContext.Should().BeSameAs(originalPolicy);
                retained.PolicyContextFingerprint.Should().Be(binding.PolicyContextFingerprint);
                retained.ExecutorAuthority.TokenExpiresAt.Should().Be(freshAuthority.TokenExpiresAt);
                // Lose the response, refresh again, then recover the original receipt.
                capture.CaptureCurrentAsync(Arg.Any<CancellationToken>()).Returns(freshPolicy with
                { AgentCredential = freshAuthority with { TokenExpiresAt = freshAuthority.TokenExpiresAt.AddHours(1) } });
                var replay = await DatasetImportAuthorityRenewalService.RenewAsync(context, store, "workspace-1", task.TaskId, 1, request);
                replay.Should().BeSameAs(receipt);
                document.DatasetImportExecutionBindings[task.TaskId].AuthorityRevision.Should().Be(2);
                await store.Received(1).SaveAsync(Arg.Any<AgentWorkspaceDocument>());
            }
            else
            {
                receipt.Should().BeNull();
                if (scenario == "postcommit-membership-revoked")
                {
                    document.DatasetImportExecutionBindings[task.TaskId].AuthorityRevision.Should().Be(2);
                    await store.Received(1).SaveAsync(Arg.Any<AgentWorkspaceDocument>());
                }
                else await store.DidNotReceive().SaveAsync(Arg.Any<AgentWorkspaceDocument>());
            }
        }
        document.DatasetImportTasks[task.TaskId].Single().Should().BeSameAs(task);
        document.DatasetImportTasks[task.TaskId].Single().UploadAdmissionDeadline.Should().Be(task.UploadAdmissionDeadline);
    }
}
