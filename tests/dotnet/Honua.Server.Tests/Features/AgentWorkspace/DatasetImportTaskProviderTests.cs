// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
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

public sealed class DatasetImportTaskProviderTests
{
    [UnitTest]
    public async Task RetainedTaskChecksExactOwnerScopeHashAndFreshDestinationWithoutDependingOnFocus()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "human-1")], "Bearer");
        CanonicalSecurityActor.StampAuthorityClaims(identity);
        var principal = new ClaimsPrincipal(identity);
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        var destination = Substitute.For<IDatasetImportDestinationAuthorizer>();
        destination.IsAuthorizedAsync(principal, "target-1", Arg.Any<DatasetImportDestination>(), Arg.Any<DatasetImportTaskOptions>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(true);
        using var services = new ServiceCollection().AddSingleton(tenant).AddSingleton(destination).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, User = principal };
        var store = Substitute.For<IAgentWorkspaceStore>();
        store.GetTargetIdAsync().Returns("target-1");
        var task = new DatasetImportTask("import-task-1", 1, "", actor, "tenant-1", "target-1", "workspace-1",
            new("public", "roads"), new("browser-file", "roads.geojson", 5, new string('a', 64)), new(ImportLoadMode.Replace), DateTimeOffset.UtcNow.AddHours(24));
        task = task with { ContentHash = DatasetImportTaskState.ContentHash(task) };
        var document = new AgentWorkspaceDocument
        { WorkspaceId = "workspace-1", TargetId = "target-1", OwnerId = actor, Members = [actor], TenantId = "tenant-1" };
        document.DatasetImportTasks[task.TaskId] = [task];
        store.GetAsync("workspace-1").Returns(document);
        var provider = new DatasetImportTaskProvider(store, new HttpContextAccessor { HttpContext = context });
        (await provider.AuthorizeAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash, false)).Should().Be(task);
        await destination.Received().IsAuthorizedAsync(principal, "target-1", task.Destination, task.Options, false, Arg.Any<CancellationToken>());
        (await provider.AuthorizeAsync(principal, "workspace-1", task.TaskId, 2, task.ContentHash, false)).Should().BeNull();
        (await provider.AuthorizeAsync(principal, "workspace-1", task.TaskId, 1, new string('b', 64), false)).Should().BeNull();
        destination.IsAuthorizedAsync(principal, "target-1", task.Destination, task.Options, false, Arg.Any<CancellationToken>()).Returns(false);
        (await provider.AuthorizeAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash, false)).Should().BeNull();
        destination.IsAuthorizedAsync(principal, "target-1", task.Destination, task.Options, false, Arg.Any<CancellationToken>()).Returns(true);
        document.DatasetImportTasks[task.TaskId] = [task with { OwnerActorId = "foreign" }];
        (await provider.AuthorizeAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash, false)).Should().BeNull();
        document.DatasetImportTasks[task.TaskId] = [task];
        tenant.TenantId.Returns("tenant-2");
        (await provider.AuthorizeAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash, false)).Should().BeNull();
        tenant.TenantId.Returns("tenant-1");
        store.GetAsync("workspace-1").Returns(document with { Members = [] });
        (await provider.AuthorizeAsync(principal, "workspace-1", task.TaskId, 1, task.ContentHash, false)).Should().BeNull();
    }
}
