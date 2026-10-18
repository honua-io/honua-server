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

public sealed class DatasetImportAuthorityDescriptorTests
{
    [UnitTheory]
    [InlineData("unchanged")]
    [InlineData("renewal")]
    [InlineData("preparation")]
    [InlineData("membership")]
    [InlineData("target")]
    [InlineData("task")]
    public async Task DescriptorNeverReturnsStaleAuthorityOrRevokedScopeAfterLookup(string change)
    {
        var keys = new InMemoryAdminApiKeyStore();
        var issued = await keys.CreateAsync("descriptor-agent", ["agent:workspace", "admin:read"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(issued.Record);
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        var tasks = Substitute.For<IDatasetImportTaskProvider>();
        using var services = new ServiceCollection().AddSingleton<IAdminApiKeyStore>(keys).AddSingleton(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        var task = new DatasetImportTask("import-task-1", 1, new string('a', 64), actor, "tenant-1", "target-1", "workspace-1",
            new("public", "new_table"), new("browser-file", "file.geojson", 123, new string('b', 64)), new(), DateTimeOffset.UtcNow.AddHours(24));
        var allowed = true;
        tasks.AuthorizeAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash, false, Arg.Any<CancellationToken>())
            .Returns(_ => allowed ? task : null);
        var snapshot = (await AgentCredentialAuthority.CaptureAsync(principal, services, default))!;
        var policy = new JobSecurityContext(actor, "tenant-1", [], "role") { OwnerActorId = actor, AgentCredential = snapshot };
        var binding = new DatasetImportExecutionBinding(task.TaskId, 1, task.ContentHash, 1, "authority-1", actor,
            "human-1", "turn-1", "original-turn-hash", policy, JobSecurityContextFingerprint.Compute(policy), snapshot);
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = "workspace-1", TargetId = "target-1", OwnerId = "human-1", AgentActorId = actor,
            TenantId = "tenant-1", Members = [actor, "human-1"], DatasetImportExecutionBindings = new() { [task.TaskId] = binding },
        };
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
                else if (change == "renewal") document = document with { DatasetImportExecutionBindings = new()
                    { [task.TaskId] = binding with { AuthorityRevision = 2, AuthorityFingerprint = "authority-2" } } };
                else if (change == "preparation") document = document with { DatasetImportPreparedAdmissions = new()
                    { [task.TaskId] = new(task.TaskId, 1, task.ContentHash, new string('c', 32), 1, binding.AuthorityFingerprint,
                        actor, "human-1", binding.OriginatingTurnId, binding.OriginalTurnHash, policy,
                        binding.PolicyContextFingerprint, snapshot, task.UploadAdmissionDeadline) } };
            }
            return observed;
        });
        var reader = new DatasetImportAuthorityDescriptorReader(store, tasks, new HttpContextAccessor { HttpContext = context });
        var first = await reader.ReadAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash);
        if (change == "unchanged") first!.AuthorityRevision.Should().Be(1);
        else first.Should().BeNull();
        if (change is "renewal" or "preparation")
        {
            // Recover a lost read from current server metadata, never guessed revision1.
            var current = await reader.ReadAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash);
            current!.TaskContentHash.Should().Be(task.ContentHash);
            current.UploadAdmissionDeadline.Should().Be(task.UploadAdmissionDeadline);
            current.AuthorityRevision.Should().Be(change == "renewal" ? 2 : 1);
            current.PreparedOperationId.Should().Be(change == "preparation" ? new string('c', 32) : null);
        }
        await store.DidNotReceive().SaveAsync(Arg.Any<AgentWorkspaceDocument>());
    }
}
