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
public sealed partial class WorkTaskCrewAccessTests : IAsyncLifetime
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
            $"/api/v1/tasks/{taskId}/completion",
            new WorkTaskCompletionRequest
            {
                CommandId = Guid.NewGuid(),
                ExpectedVersion = 3,
                ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                Form = _form,
                Answers = JsonElement.Parse("""{"name":"Trail clear"}"""),
