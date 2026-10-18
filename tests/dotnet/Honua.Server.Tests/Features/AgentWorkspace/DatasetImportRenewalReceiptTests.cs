// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
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

public sealed class DatasetImportRenewalReceiptTests
{
    [UnitTheory]
    [InlineData("membership")]
    [InlineData("target")]
    [InlineData("task")]
    [InlineData("task-deleted-after-authorization")]
    [InlineData("task-deadline-after-authorization")]
    [InlineData("later-authority")]
    [InlineData("foreign-revision")]
    [InlineData("foreign-hash")]
    public async Task OriginalReceiptRechecksAuthorityAfterLookupAndNeverRenewsTwice(string change)
    {
        var keys = new InMemoryAdminApiKeyStore();
        var issued = await keys.CreateAsync("renewal-agent", ["agent:workspace", "admin:read"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(issued.Record);
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        var tasks = Substitute.For<IDatasetImportTaskProvider>();
        using var services = new ServiceCollection().AddSingleton<IAdminApiKeyStore>(keys).AddSingleton(tenant).AddSingleton(tasks).BuildServiceProvider();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        var task = new DatasetImportTask("import-task-1", 1, new string('a', 64), actor, "tenant-1", "target-1", "workspace-1",
            new("public", "new_table"), new("browser-file", "file.geojson", 123, new string('b', 64)), new(), DateTimeOffset.UtcNow.AddHours(24));
        var allowed = true;
        tasks.AuthorizeAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash, false, Arg.Any<CancellationToken>())
            .Returns(_ => allowed ? task : null);
        var snapshot = (await AgentCredentialAuthority.CaptureAsync(principal, services, default))!;
        var policy = new JobSecurityContext(actor, "tenant-1", [], "role") { OwnerActorId = actor, AgentCredential = snapshot };
        var binding = new DatasetImportExecutionBinding(task.TaskId, 1, task.ContentHash, 3, "later-authority-fingerprint", actor,
            "human-1", "turn-1", "original-turn-hash", policy, JobSecurityContextFingerprint.Compute(policy), snapshot);
        var original = new DatasetImportAuthorityRenewal("renewal-1", 1, 2, "original-renewed-authority", DateTimeOffset.UtcNow);
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = "workspace-1", TargetId = "target-1", OwnerId = "human-1", AgentActorId = actor,
            TenantId = "tenant-1", Members = [actor, "human-1"], DatasetImportTasks = new() { [task.TaskId] = [task] },
            DatasetImportExecutionBindings = new() { [task.TaskId] = binding },
            DatasetImportAuthorityRenewals = new() { [DatasetImportTaskState.RenewalKey(task.TaskId, change == "foreign-revision" ? task.Revision + 1 : task.Revision,
                change == "foreign-hash" ? new string('b', 64) : task.ContentHash, original.RenewalId)] = original },
        };
        var authorizations = 0;
        tasks.AuthorizeAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash, false, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++authorizations == 2)
            {
                if (change == "task-deleted-after-authorization") document = document with { DatasetImportTasks = [] };
                else if (change == "task-deadline-after-authorization") document = document with { DatasetImportTasks = new()
                    { [task.TaskId] = [task with { UploadAdmissionDeadline = task.UploadAdmissionDeadline.AddHours(1) }] } };
            }
            return allowed ? task : null;
        });
        var store = Substitute.For<IAgentWorkspaceStore>();
        var target = "target-1";
        store.GetTargetIdAsync().Returns(_ => target);
        var reads = 0;
        store.GetAsync("workspace-1").Returns(_ =>
        {
            var observed = document;
            if (++reads == 1)
            {
                if (change == "membership") document = document with { Members = ["human-1"] };
                else if (change == "target") target = "target-2";
                else if (change == "task") allowed = false;
            }
            return observed;
        });
        if (change is "foreign-revision" or "foreign-hash")
        {
            await FluentActions.Awaiting(async () => await DatasetImportAuthorityRenewalService.RenewAsync(context, store,
                "workspace-1", task.TaskId, 1, new(original.RenewalId, original.PreviousAuthorityRevision, task.ContentHash)))
                .Should().ThrowAsync<AgentWorkspaceConflictException>();
            await store.DidNotReceive().SaveAsync(Arg.Any<AgentWorkspaceDocument>());
            return;
        }
        var recovered = await DatasetImportAuthorityRenewalService.RenewAsync(context, store, "workspace-1", task.TaskId, 1,
            new(original.RenewalId, original.PreviousAuthorityRevision, task.ContentHash));
        if (change == "later-authority") recovered.Should().BeSameAs(original);
        else recovered.Should().BeNull();
        await store.DidNotReceive().SaveAsync(Arg.Any<AgentWorkspaceDocument>());
    }
}
