// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Console;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Console.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class ProposalEndpointsTests
{
    [IntegrationTheory]
    [InlineData("new-owner")]
    [InlineData(null)]
    [Endpoint("PUT /api/v1/console/content/{id}")]
    public async Task ContentOwnership_AgentKey_CannotTransferOrClearOwner(string? newOwner)
    {
        using var human = await CreateKeyClientAsync("human-content-owner", HumanKeyPermissions);
        using var agent = await CreateKeyClientAsync("agent-content-owner", AgentKeyPermissions);
        var name = $"ownership-{Guid.NewGuid():N}";
        using var created = await human.PostAsJsonAsync("/api/v1/console/content",
            new { name, itemType = "dashboard", visibility = "personal", ownerId = "original-owner" });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        using var original = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var data = original.RootElement.GetProperty("data");
        var path = $"/api/v1/console/content/{data.GetProperty("id").GetString()}";
        var update = new
        {
            name,
            itemType = "dashboard",
            visibility = "personal",
            ownerId = newOwner,
            generation = data.GetProperty("generation").GetInt64(),
        };

        using var denied = await agent.PutAsJsonAsync(path, update);
        await AssertAgentApprovalRefusalAsync(denied, ConsoleGuidance, AgentApprovalFilter.ContentVisibilityRef);
        using var unchanged = await human.GetAsync(path);
        unchanged.StatusCode.Should().Be(HttpStatusCode.OK);
        using var stored = JsonDocument.Parse(await unchanged.Content.ReadAsStringAsync());
        stored.RootElement.GetProperty("data").GetProperty("ownerId").GetString().Should().Be("original-owner");
        stored.RootElement.GetProperty("data").GetProperty("generation").GetInt64()
            .Should().Be(data.GetProperty("generation").GetInt64());

        using var allowed = await human.PutAsJsonAsync(path, update);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
        using var transferred = JsonDocument.Parse(await allowed.Content.ReadAsStringAsync());
        transferred.RootElement.GetProperty("data").GetProperty("ownerId").GetString().Should().Be(newOwner);
    }

    [IntegrationTheory]
    [InlineData("new-owner")]
    [InlineData(null)]
    [Endpoint("PUT /api/v1/console/content/{id}")]
    public async Task ContentOwnership_OmittedGeneration_RejectsConcurrentOwnerChange(string? newOwner)
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ProjectedConsoleContentStore>();
        var original = await SeedPersonalItemAsync(store, "ownership-race", "original-owner");
        var context = AgentContextFor(scope.ServiceProvider, original);
        var request = new UpdateConsoleContentItemRequest
        {
            Name = original.Name,
            ItemType = original.ItemType,
            Visibility = ConsoleVisibility.Personal,
            OwnerId = original.OwnerId,
        };
        var delegated = false;

        await AgentApprovalFilter.ContentVisibility(EndpointFilterInvocationContext.Create(context, request), async invocation =>
        {
            delegated = true;
            // A human changes the owner after authorization and before the handler reads or writes.
            await store.UpdateAsync(original with { OwnerId = newOwner });
            var current = (await store.GetAsync(original.Id))!;
            var boundRequest = invocation.GetArgument<UpdateConsoleContentItemRequest>(0);
            var projected = ConsoleContentMapper.FromUpdateRequest(original.Id, boundRequest, current, context.User);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(projected));
            return null;
        });

        delegated.Should().BeTrue();
        var persisted = (await store.GetAsync(original.Id))!;
        persisted.OwnerId.Should().Be(newOwner);
        persisted.Generation.Should().Be(original.Generation + 1);
    }

    [IntegrationTheory]
    [InlineData("new-owner")]
    [InlineData(null)]
    [Endpoint("PUT /api/v1/console/content/{id}")]
    public async Task ContentOwnership_FutureGeneration_RejectsBeforeHandler(string? newOwner)
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ProjectedConsoleContentStore>();
        var original = await SeedPersonalItemAsync(store, "ownership-future-generation", "original-owner");
        var context = AgentContextFor(scope.ServiceProvider, original);
        var request = new UpdateConsoleContentItemRequest
        {
            Name = original.Name,
            ItemType = original.ItemType,
            Visibility = ConsoleVisibility.Personal,
            OwnerId = original.OwnerId,
            Generation = original.Generation + 1,
        };
        var delegated = false;

        var result = await AgentApprovalFilter.ContentVisibility(
            EndpointFilterInvocationContext.Create(context, request), async invocation =>
            {
                delegated = true;
                await store.UpdateAsync(original with { OwnerId = newOwner });
                var current = (await store.GetAsync(original.Id))!;
                var boundRequest = invocation.GetArgument<UpdateConsoleContentItemRequest>(0);
                var projected = ConsoleContentMapper.FromUpdateRequest(original.Id, boundRequest, current, context.User);
                await store.UpdateAsync(projected);
                return Results.Ok();
            });

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        delegated.Should().BeFalse();
        var persisted = (await store.GetAsync(original.Id))!;
        persisted.OwnerId.Should().Be(original.OwnerId);
        persisted.Generation.Should().Be(original.Generation);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/content")]
    public async Task ContentOwnership_AgentKey_CannotCreateForAnotherOwner()
    {
        using var agent = await CreateKeyClientAsync("agent-content-create", AgentKeyPermissions);
        using var denied = await agent.PostAsJsonAsync("/api/v1/console/content", new
        {
            name = $"delegated-{Guid.NewGuid():N}",
            itemType = "dashboard",
            visibility = "personal",
            ownerId = "another-owner",
        });
        await AssertAgentApprovalRefusalAsync(denied, ConsoleGuidance, AgentApprovalFilter.ContentVisibilityRef);

        using var own = await agent.PostAsJsonAsync("/api/v1/console/content",
            new { name = $"own-{Guid.NewGuid():N}", itemType = "dashboard", visibility = "personal" });
        own.StatusCode.Should().Be(HttpStatusCode.Created, await own.Content.ReadAsStringAsync());
        using var ownContent = JsonDocument.Parse(await own.Content.ReadAsStringAsync());
        var ownData = ownContent.RootElement.GetProperty("data");
        using var edited = await agent.PutAsJsonAsync($"/api/v1/console/content/{ownData.GetProperty("id").GetString()}", new
        {
            name = $"edited-{Guid.NewGuid():N}",
            itemType = "dashboard",
            visibility = "personal",
            ownerId = ownData.GetProperty("ownerId").GetString(),
            generation = ownData.GetProperty("generation").GetInt64(),
        });
        edited.StatusCode.Should().Be(HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());
        using var patched = await agent.PatchAsJsonAsync($"/api/v1/console/content/{ownData.GetProperty("id").GetString()}",
            new { title = "patched by its owner" });
        patched.StatusCode.Should().Be(HttpStatusCode.OK, await patched.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/console/content/{id}")]
    [Endpoint("PATCH /api/v1/console/content/{id}")]
    public async Task ContentEdit_AgentKey_CannotEditAnotherOwnersItem()
    {
        using var human = await CreateKeyClientAsync("human-content-edit", HumanKeyPermissions);
        using var agent = await CreateKeyClientAsync("agent-content-edit", AgentKeyPermissions);
        var (path, original) = await CreateContentAsync(human, new { name = $"owned-{Guid.NewGuid():N}", itemType = "map", visibility = "personal" });

        using var replaced = await agent.PutAsJsonAsync(path, new
        {
            name = "renamed-by-agent",
            itemType = "map",
            visibility = "personal",
            ownerId = original.GetProperty("ownerId").GetString(),
        });
        using var patched = await agent.PatchAsJsonAsync(path, new { title = "patched by agent" });

        await AssertAgentApprovalRefusalAsync(replaced, ConsoleGuidance, AgentApprovalFilter.ContentVisibilityRef);
        await AssertAgentApprovalRefusalAsync(patched, ConsoleGuidance, AgentApprovalFilter.ContentVisibilityRef);
        await AssertContentUnchangedAsync(human, path, original);
        using var byOwner = await human.PatchAsJsonAsync(path, new { title = "patched by its owner" });
        byOwner.StatusCode.Should().Be(HttpStatusCode.OK, await byOwner.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/console/content/{id}")]
    [Endpoint("PATCH /api/v1/console/content/{id}")]
    public async Task ContentEdit_AgentKey_CannotEditItsItemOnceOthersCanSeeIt()
    {
        using var human = await CreateKeyClientAsync("human-content-shared", HumanKeyPermissions);
        using var agent = await CreateKeyClientAsync("agent-content-shared", AgentKeyPermissions);
        var (path, own) = await CreateContentAsync(agent, new { name = $"agent-made-{Guid.NewGuid():N}", itemType = "map", visibility = "personal" });
        var agentOwner = own.GetProperty("ownerId").GetString();
        using var shared = await human.PatchAsJsonAsync(path, new { visibility = "organization" });
        shared.StatusCode.Should().Be(HttpStatusCode.OK, await shared.Content.ReadAsStringAsync());
        using var sharedDocument = JsonDocument.Parse(await shared.Content.ReadAsStringAsync());
        var sharedItem = sharedDocument.RootElement.GetProperty("data").Clone();

        using var patched = await agent.PatchAsJsonAsync(path, new { title = "patched by agent" });
        using var replaced = await agent.PutAsJsonAsync(path, new { name = "renamed-by-agent", itemType = "map", visibility = "personal", ownerId = agentOwner });

        await AssertAgentApprovalRefusalAsync(patched, ConsoleGuidance, AgentApprovalFilter.ContentVisibilityRef);
        await AssertAgentApprovalRefusalAsync(replaced, ConsoleGuidance, AgentApprovalFilter.ContentVisibilityRef);
        await AssertContentUnchangedAsync(human, path, sharedItem);
    }

    [IntegrationTest]
    [Endpoint("PATCH /api/v1/console/content/{id}")]
    public async Task ContentEdit_AgentKey_CannotEditItsItemOnceSharedByTier()
    {
        using var human = await CreateKeyClientAsync("human-content-tier", HumanKeyPermissions);
        using var agent = await CreateKeyClientAsync("agent-content-tier", AgentKeyPermissions);
        var (path, own) = await CreateContentAsync(agent, new { name = $"agent-tier-{Guid.NewGuid():N}", itemType = "map", visibility = "personal" });
        using var tier = await human.PutAsJsonAsync($"{path}/share/access", new { accessTier = "organization" });
        tier.StatusCode.Should().Be(HttpStatusCode.OK, await tier.Content.ReadAsStringAsync());

        using var patched = await agent.PatchAsJsonAsync(path, new { title = "patched by agent" });

        await AssertAgentApprovalRefusalAsync(patched, ConsoleGuidance, AgentApprovalFilter.ContentVisibilityRef);
        await AssertContentUnchangedAsync(human, path, own);
    }

    [IntegrationTest]
    [Endpoint("PATCH /api/v1/console/content/{id}")]
    public async Task ContentPatch_OwnerChangedAfterAuthorization_IsRejectedInsideTheWrite()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ProjectedConsoleContentStore>();
        var original = await SeedPersonalItemAsync(store, "patch-ownership-race", "agent-owner");
        var context = AgentContextFor(scope.ServiceProvider, original);
        var delegated = false;

        await AgentApprovalFilter.ContentVisibility(
            EndpointFilterInvocationContext.Create(context, new PatchConsoleContentItemRequest { Title = "patched by agent" }),
            async invocation =>
            {
                delegated = true;
                // A person transfers the item after authorization and before the handler writes.
                await store.UpdateAsync(original with { OwnerId = "a-person" });
                var patch = ConsoleContentMapper.FromPatchRequest(invocation.GetArgument<PatchConsoleContentItemRequest>(0), context.User);
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.PatchAsync(original.Id, patch));
                return null;
            });

        delegated.Should().BeTrue();
        var persisted = (await store.GetAsync(original.Id))!;
        persisted.OwnerId.Should().Be("a-person");
        persisted.Title.Should().Be(original.Title);
    }

    private static Task<ConsoleContentItem> SeedPersonalItemAsync(ProjectedConsoleContentStore store, string name, string ownerId)
        => store.CreateAsync(new ConsoleContentItem
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            ItemType = ConsoleContentItemType.Dashboard,
            Visibility = ConsoleVisibility.Personal,
            OwnerId = ownerId,
        });

    // An agent request for the item, made by the item's owner.
    private static DefaultHttpContext AgentContextFor(IServiceProvider services, ConsoleContentItem item)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, "agent"), new Claim(ClaimTypes.NameIdentifier, item.OwnerId!)], "test")),
        };
        context.Request.RouteValues["id"] = item.Id;
        return context;
    }

    private static async Task<(string Path, JsonElement Item)> CreateContentAsync(HttpClient client, object request)
    {
        using var created = await client.PostAsJsonAsync("/api/v1/console/content", request);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var item = document.RootElement.GetProperty("data").Clone();
        return ($"/api/v1/console/content/{item.GetProperty("id").GetString()}", item);
    }

    private static async Task AssertContentUnchangedAsync(HttpClient reader, string path, JsonElement expected)
    {
        using var response = await reader.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var stored = document.RootElement.GetProperty("data");
        foreach (var field in new[] { "name", "title", "ownerId", "visibility", "updatedAt" })
        {
            stored.TryGetProperty(field, out var actual);
            expected.TryGetProperty(field, out var wanted);
            actual.ToString().Should().Be(wanted.ToString(), field);
        }
    }
}
