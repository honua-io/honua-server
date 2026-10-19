// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
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

public sealed class DatasetImportTaskContextReaderTests
{
    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedAgentMayExplainExactHumanTaskButNeverBorrowTransferOwnership(bool revokeDuringRead)
    {
        var keys = new InMemoryAdminApiKeyStore();
        var created = await keys.CreateAsync("linked-agent", ["admin:read", "agent:workspace"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        var destination = Substitute.For<IDatasetImportDestinationAuthorizer>();
        destination.IsAuthorizedAsync(principal, "target-1", Arg.Any<DatasetImportDestination>(), Arg.Any<DatasetImportTaskOptions>(), false,
            Arg.Any<CancellationToken>()).Returns(true);
        using var services = new ServiceCollection().AddSingleton<IAdminApiKeyStore>(keys).AddSingleton(tenant).AddSingleton(destination).BuildServiceProvider();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        var store = Substitute.For<IAgentWorkspaceStore>();
        store.GetTargetIdAsync().Returns("target-1");
        var task = new DatasetImportTask("import-task-1", 1, "", "human-owner", "tenant-1", "target-1", "workspace-1",
            new("public", "roads"), new("browser-file", "roads.geojson", 5, new string('a', 64)), new(), DateTimeOffset.UtcNow.AddHours(24));
        task = task with { ContentHash = DatasetImportTaskState.ContentHash(task) };
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = "workspace-1",
            TargetId = "target-1",
            OwnerId = "human-owner",
            AgentActorId = actor,
            Members = ["human-owner", actor],
            TenantId = "tenant-1"
        };
        document.DatasetImportTasks[task.TaskId] = [task];
        store.GetAsync("workspace-1").Returns(_ => document);
        var provider = new DatasetImportTaskProvider(store, new HttpContextAccessor { HttpContext = context });
        (await provider.ReadAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash)).Should().Be(task);
        // The transfer port remains exact owner-only even for this linked context observer.
        (await provider.AuthorizeAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash, false)).Should().BeNull();
        (await provider.AuthorizeAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash, true)).Should().BeNull();
        (await provider.ReadAsync(principal, "workspace-1", task.TaskId, 2, task.ContentHash)).Should().BeNull();
        document = document with { AgentActorId = "some-other-agent" };
        (await provider.ReadAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash)).Should().BeNull();
        document = document with { AgentActorId = actor, OwnerId = "different-workspace-owner" };
        (await provider.ReadAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash)).Should().BeNull();
        document = document with { OwnerId = "human-owner" };
        destination.IsAuthorizedAsync(principal, "target-1", task.Destination, task.Options, false, Arg.Any<CancellationToken>()).Returns(false);
        (await provider.ReadAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash)).Should().BeNull();
        var delayed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        destination.IsAuthorizedAsync(principal, "target-1", task.Destination, task.Options, false, Arg.Any<CancellationToken>()).Returns(delayed.Task);
        var pending = provider.ReadAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash);
        pending.IsCompleted.Should().BeFalse();
        if (revokeDuringRead) await keys.RevokeAsync(created.Record.Id, default);
        else document = document with { AgentActorId = null };
        delayed.SetResult(true);
        (await pending).Should().BeNull();
    }
}
