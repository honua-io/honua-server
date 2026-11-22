// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Forms;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class WorkTaskEndpointsTests
{
    private static WebAppFixture CreateAuthenticatedFixture() => new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        })
        .ConfigureServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, RlsClaimsTestAuthHandler>(RlsClaimsTestAuthHandler.SchemeName, _ => { })
                .AddPolicyScheme("TestOrApiKey", "Test or API key", options =>
                {
                    options.ForwardDefaultSelector = context => context.Request.Headers.ContainsKey("X-API-Key")
                        ? "ApiKey"
                        : RlsClaimsTestAuthHandler.SchemeName;
                });
            services.PostConfigureAll<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = "TestOrApiKey";
                options.DefaultChallengeScheme = "TestOrApiKey";
                options.DefaultScheme = "TestOrApiKey";
            });
        });

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/tasks")]
    public async Task CreateTask_Anonymous_CannotWrite()
    {
        using var client = _fixture.CreateClient();
        var response = await client.PostAsJsonAsync(TasksPath, RepairRequest(), AssetsJsonContext.Default.WorkTaskCreateRequest);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AssetTestData.CountAsync(_fixture, "work_tasks")).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/tasks")]
    public async Task CreateTask_Reader_CannotWrite()
    {
        using var client = _fixture.CreateClient();
        SetPrincipal(client, "task-reader", "reader");
        var response = await client.PostAsJsonAsync(TasksPath, RepairRequest(), AssetsJsonContext.Default.WorkTaskCreateRequest);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AssetTestData.CountAsync(_fixture, "work_tasks")).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/tasks")]
    public async Task CreateTask_ServiceEditor_CanReportUnassignedRequest()
    {
        using var client = _fixture.CreateClient();
        SetPrincipal(client, "crew-a", $"data-editor:{WebAppFixture.TestServiceId}");
        var response = await client.PostAsJsonAsync(TasksPath, RepairRequest() with { Type = WorkTaskType.Request },
            AssetsJsonContext.Default.WorkTaskCreateRequest);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        (await AssetTestData.CountAsync(_fixture, "work_tasks")).Should().Be(1);
        (await AssetTestData.CountAsync(_fixture, "work_task_history")).Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/transitions")]
    public async Task StartTask_AssignedCrewByUserName_StartsAndOthersAreForbidden()
    {
        var taskId = await CreateRepairAsync();
        var path = $"{TasksPath}/{taskId}/transitions";
        (await PostAsync(path, Transition(WorkTaskAction.Assign, 1) with { Assignee = "crew-a" })).StatusCode.Should().Be(HttpStatusCode.OK);
        using var crewA = CrewClient("crew-a");
        using var crewB = CrewClient("crew-b");

        var otherCrew = await crewB.PostAsJsonAsync(path, Transition(WorkTaskAction.Start, 2), AssetsJsonContext.Default.WorkTaskTransitionRequest);
        var cancel = await crewA.PostAsJsonAsync(path, Transition(WorkTaskAction.Cancel, 2) with { Reason = "Not mine" },
            AssetsJsonContext.Default.WorkTaskTransitionRequest);
        var start = await crewA.PostAsJsonAsync(path, Transition(WorkTaskAction.Start, 2), AssetsJsonContext.Default.WorkTaskTransitionRequest);

        otherCrew.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        cancel.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        start.StatusCode.Should().Be(HttpStatusCode.OK, await start.Content.ReadAsStringAsync());
        var task = await _fixture.Client.GetFromJsonAsync($"{TasksPath}/{taskId}", AssetsJsonContext.Default.WorkTask);
        task!.Status.Should().Be(WorkTaskStatus.InProgress);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /api/v1/tasks")]
    public async Task ListTasks_LayerScopedApiKey_ReturnsOnlyAuthorizedLayerTasksAndHidesAssetless()
    {
        var assetTask = await PostAsync(TasksPath, InspectionRequest(_assets[0].AssetId));
        assetTask.StatusCode.Should().Be(HttpStatusCode.Created, await assetTask.Content.ReadAsStringAsync());
        var assetTaskId = Guid.Parse((await AssetTestData.ReadJsonAsync(assetTask))["task"]!["taskId"]!.GetValue<string>());
        await CreateRepairAsync();

        var snapshot = await _fixture.Services.GetRequiredService<IMetadataV2GraphProvider>().GetCurrentAsync();
        var target = FormTargetMetadataResolver.Resolve(snapshot,
            new FormTargetDefinition { ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId });
        var keyStore = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var layerKey = await keyStore.CreateAsync("layer-crew",
            [$"write:{target.Service!.Metadata.Name}/{target.Resource!.Metadata.Name}"], DateTimeOffset.UtcNow.AddMinutes(5), "test", CancellationToken.None);
        var otherLayerKey = await keyStore.CreateAsync("other-layer-crew",
            [$"write:{target.Service!.Metadata.Name}/other-layer"], DateTimeOffset.UtcNow.AddMinutes(5), "test", CancellationToken.None);
        try
        {
            using var layerClient = _fixture.CreateClient();
            layerClient.DefaultRequestHeaders.Add("X-API-Key", layerKey.Key);
            var allowed = await layerClient.GetAsync($"{TasksPath}?serviceId={WebAppFixture.TestServiceId}");
            allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
            var result = await allowed.Content.ReadFromJsonAsync(AssetsJsonContext.Default.WorkTaskListResult);
            result!.Items.Select(static item => item.TaskId).Should().Equal(assetTaskId);

            using var otherClient = _fixture.CreateClient();
            otherClient.DefaultRequestHeaders.Add("X-API-Key", otherLayerKey.Key);
            var denied = await otherClient.GetAsync($"{TasksPath}?serviceId={WebAppFixture.TestServiceId}");
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await keyStore.RevokeAsync(layerKey.Record.Id, CancellationToken.None);
            await keyStore.RevokeAsync(otherLayerKey.Record.Id, CancellationToken.None);
        }
    }

    private HttpClient CrewClient(string user)
    {
        var client = _fixture.CreateClient();
        SetPrincipal(client, user, $"data-editor:{WebAppFixture.TestServiceId}");
        return client;
    }

    private static void SetPrincipal(HttpClient client, string user, string role)
    {
        client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.RolesHeader, role);
    }
}
