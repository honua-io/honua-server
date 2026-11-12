// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Assets;

// Real API keys, no dev-auth bypass: planners hold admin:write, crew hold write:<service>, outsiders another service.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class WorkTaskCrewAccessTests : IAsyncLifetime
{
    private const string TasksPath = "/api/v1/tasks";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });

    private HttpClient _planner = null!;
    private HttpClient _crew = null!;
    private HttpClient _otherCrew = null!;
    private HttpClient _outsider = null!;
    private WorkTaskFormReference _form = null!;

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Join("tests", "seed", "server.yaml"));
        await _fixture.InitializeAsync();
        await AssetTestData.ResetAsync(_fixture);
        _planner = await ClientAsync("planner", "admin:write");
        _crew = await ClientAsync("crew-a", $"write:{WebAppFixture.TestServiceId}");
        _otherCrew = await ClientAsync("crew-b", $"write:{WebAppFixture.TestServiceId}");
        _outsider = await ClientAsync("outsider", "write:another-service");
        _form = await AssetTestData.PublishFormAsync(_fixture);
    }

    public async Task DisposeAsync()
    {
        foreach (var client in new[] { _planner, _crew, _otherCrew, _outsider })
        {
            client?.Dispose();
        }

        await AssetTestData.ResetAsync(_fixture);
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /api/v1/tasks")]
    [Endpoint("POST /api/v1/tasks/{taskId}/transitions")]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Crew_ReportsStartsAndCompletesOwnWork_ButCannotPlan()
    {
        var reported = await PostAsync(_crew, TasksPath, Request(WorkTaskType.Request, assignee: null));
        var plannedByCrew = await PostAsync(_crew, TasksPath, Request(WorkTaskType.Repair, assignee: "someone"));

        reported.StatusCode.Should().Be(HttpStatusCode.Created, await reported.Content.ReadAsStringAsync());
        plannedByCrew.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var task = (await AssetTestData.ReadJsonAsync(reported))["task"]!;
        var taskId = task["taskId"]!.GetValue<string>();
        var crewId = task["createdBy"]!.GetValue<string>();

        var selfAssign = await PostAsync(_crew, $"{TasksPath}/{taskId}/transitions", Transition(WorkTaskAction.Assign, 1, crewId));
        var assigned = await PostAsync(_planner, $"{TasksPath}/{taskId}/transitions", Transition(WorkTaskAction.Assign, 1, crewId));
        var started = await PostAsync(_crew, $"{TasksPath}/{taskId}/transitions", Transition(WorkTaskAction.Start, 2, null));
        var completed = await _crew.PostAsJsonAsync(
            $"{TasksPath}/{taskId}/completion",
            new WorkTaskCompletionRequest
            {
                CommandId = Guid.NewGuid(),
                ExpectedVersion = 3,
                ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                Form = _form,
                Notes = "Branch removed from the trail.",
            },
            AssetsJsonContext.Default.WorkTaskCompletionRequest);

        selfAssign.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        assigned.StatusCode.Should().Be(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync());
        started.StatusCode.Should().Be(HttpStatusCode.OK, await started.Content.ReadAsStringAsync());
        completed.StatusCode.Should().Be(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync());
        (await AssetTestData.ReadJsonAsync(completed))["serviceRecordId"].Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /api/v1/tasks/{taskId}")]
    [Endpoint("POST /api/v1/tasks/{taskId}/transitions")]
    public async Task OtherPrincipals_CannotSeeOrMoveSomeoneElsesTask()
    {
        var reported = await PostAsync(_crew, TasksPath, Request(WorkTaskType.Request, assignee: null));
        var task = (await AssetTestData.ReadJsonAsync(reported))["task"]!;
        var taskId = task["taskId"]!.GetValue<string>();
        (await PostAsync(_planner, $"{TasksPath}/{taskId}/transitions", Transition(WorkTaskAction.Assign, 1, task["createdBy"]!.GetValue<string>())))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var outsiderRead = await _outsider.GetAsync($"{TasksPath}/{taskId}");
        var outsiderMove = await PostAsync(_outsider, $"{TasksPath}/{taskId}/transitions", Transition(WorkTaskAction.Start, 2, null));
        var otherCrewStale = await PostAsync(_otherCrew, $"{TasksPath}/{taskId}/transitions", Transition(WorkTaskAction.Start, 9, null));

        outsiderRead.StatusCode.Should().Be(HttpStatusCode.NotFound);
        outsiderMove.StatusCode.Should().Be(HttpStatusCode.NotFound);
        otherCrewStale.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await otherCrewStale.Content.ReadAsStringAsync()).Should().NotContain("currentVersion");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /api/v1/tasks")]
    [Endpoint("POST /api/v1/tasks")]
    public async Task Anonymous_IsRefusedEvenWhenTheServiceAllowsAnonymousWrites()
    {
        _fixture.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, accessPolicy: new AccessPolicy { AllowAnonymousWrite = true });
        using var anonymous = _fixture.CreateClient();

        var list = await anonymous.GetAsync($"{TasksPath}?serviceId={WebAppFixture.TestServiceId}");
        var create = await PostAsync(anonymous, TasksPath, Request(WorkTaskType.Request, assignee: null));

        list.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        create.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AssetTestData.CountAsync(_fixture, "work_tasks")).Should().Be(0);
    }

    private WorkTaskCreateRequest Request(string type, string? assignee) => new()
    {
        CommandId = Guid.NewGuid(),
        TaskId = Guid.NewGuid(),
        ServiceId = WebAppFixture.TestServiceId,
        Type = type,
        Title = "Fallen branch on trail",
        Location = JsonElement.Parse("""{"type":"Point","coordinates":[-156.47,20.89]}"""),
        Assignee = assignee,
        FormId = _form.FormId,
    };

    private static WorkTaskTransitionRequest Transition(string action, long expectedVersion, string? assignee)
        => new() { CommandId = Guid.NewGuid(), ExpectedVersion = expectedVersion, Action = action, Assignee = assignee };

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, WorkTaskCreateRequest request)
        => client.PostAsJsonAsync(path, request, AssetsJsonContext.Default.WorkTaskCreateRequest);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, WorkTaskTransitionRequest request)
        => client.PostAsJsonAsync(path, request, AssetsJsonContext.Default.WorkTaskTransitionRequest);

    // The key name is the principal's identity, so each role gets its own.
    private async Task<HttpClient> ClientAsync(string name, string permission)
    {
        var store = _fixture.Services.GetRequiredService<Honua.Infrastructure.Authentication.IAdminApiKeyStore>();
        var created = await store.CreateAsync(name, [permission], DateTimeOffset.UtcNow.AddMinutes(30), "test", CancellationToken.None);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", created.Key);
        return client;
    }
}
