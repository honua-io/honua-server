// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Assets;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class WorkTaskEndpointsTests : IAsyncLifetime
{
    private const string TasksPath = "/api/v1/tasks";

    private readonly WebAppFixture _fixture = new();
    private Guid _profileId;
    private IReadOnlyList<(string AssetId, double X)> _assets = [];
    private WorkTaskFormReference _form = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await AssetTestData.ResetAsync(_fixture);
        _form = await AssetTestData.PublishFormAsync(_fixture);
        _assets = await AssetTestData.DiscoverAssetsAsync(_fixture);
        _profileId = await AssetTestData.CreateActiveProfileAsync(_fixture.Client, _form);
    }

    public async Task DisposeAsync()
    {
        await AssetTestData.ResetAsync(_fixture);
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/tasks")]
    public async Task CreateTask_FromAsset_CopiesGeometryPinsFormAndReplaysOnce()
    {
        var request = InspectionRequest(_assets[0].AssetId);

        var created = await PostAsync(TasksPath, request);
        var replay = await PostAsync(TasksPath, request);
        var reused = await PostAsync(TasksPath, request with { Title = "Different" });

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var task = (await AssetTestData.ReadJsonAsync(created))["task"]!;
        task["status"]!.GetValue<string>().Should().Be(WorkTaskStatus.Assigned);
        task["geometrySource"]!.GetValue<string>().Should().Be("asset");
        task["geometry"]!["coordinates"]![0]!.GetValue<double>().Should().BeApproximately(_assets[0].X, 1e-9);
        task["form"]!["version"]!.GetValue<int>().Should().Be(_form.Version);
        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        (await AssetTestData.ReadJsonAsync(replay))["outcome"]!.GetValue<string>().Should().Be(AssetCommandOutcome.Duplicate);
        reused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AssetTestData.CountAsync(_fixture, "work_tasks")).Should().Be(1);
        (await AssetTestData.CountAsync(_fixture, "work_task_history")).Should().Be(1);

        var paddedId = await PostAsync(TasksPath, InspectionRequest("00" + _assets[0].AssetId));
        (await AssetTestData.ReadJsonAsync(paddedId))["task"]!["assetId"]!.GetValue<string>()
            .Should().Be(_assets[0].AssetId, "numeric asset IDs are stored in canonical form so one asset has one summary");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/tasks")]
    public async Task CreateTask_InvalidTargets_ReturnFieldErrors()
    {
        var noAsset = await PostAsync(TasksPath, InspectionRequest(_assets[0].AssetId) with { ProfileId = null, AssetId = null });
        var unknownAsset = await PostAsync(TasksPath, InspectionRequest("no-such-asset"));
        var noLocation = await PostAsync(TasksPath, RepairRequest() with { Location = null });
        var noForm = await PostAsync(TasksPath, RepairRequest() with { FormId = null });

        (await FieldsAsync(noAsset)).Should().Equal("assetId");
        (await FieldsAsync(unknownAsset)).Should().Equal("assetId");
        (await FieldsAsync(noLocation)).Should().Equal("location");
        (await FieldsAsync(noForm)).Should().Equal("formId");
        (await AssetTestData.CountAsync(_fixture, "work_tasks")).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/transitions")]
    public async Task Transition_TwoCommandsAtOneVersion_ExactlyOneWinsAndReplayAddsNoHistory()
    {
        var taskId = await CreateRepairAsync();
        var first = Transition(WorkTaskAction.Assign, expectedVersion: 1) with { Assignee = "crew-a" };
        var second = Transition(WorkTaskAction.Assign, expectedVersion: 1) with { Assignee = "crew-b" };

        var responses = await Task.WhenAll(
            PostAsync($"{TasksPath}/{taskId}/transitions", first),
            PostAsync($"{TasksPath}/{taskId}/transitions", second));
        var winner = responses[0].StatusCode == HttpStatusCode.OK ? first : second;
        var replay = await PostAsync($"{TasksPath}/{taskId}/transitions", winner);

        var reusedWithOtherBody = await PostAsync($"{TasksPath}/{taskId}/transitions", winner with { Assignee = "crew-c" });

        responses.Select(response => response.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        reusedWithOtherBody.StatusCode.Should().Be(HttpStatusCode.Conflict);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AssetTestData.ReadJsonAsync(replay))["outcome"]!.GetValue<string>().Should().Be(AssetCommandOutcome.Duplicate);
        (await AssetTestData.CountAsync(_fixture, "work_task_history")).Should().Be(2);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/transitions")]
    public async Task Transition_RefusedCommands_LeaveTheTaskUnchanged()
    {
        var taskId = await CreateRepairAsync();
        var path = $"{TasksPath}/{taskId}/transitions";

        var noReason = await PostAsync(path, Transition(WorkTaskAction.Cancel, 1) with { Reason = null });
        var wrongState = await PostAsync(path, Transition(WorkTaskAction.Start, 1));
        var stale = await PostAsync(path, Transition(WorkTaskAction.Assign, 7) with { Assignee = "crew-a" });
        var cancelled = await PostAsync(path, Transition(WorkTaskAction.Cancel, 1) with { Reason = "Duplicate report" });
        var reopen = await PostAsync(path, Transition(WorkTaskAction.Assign, 2) with { Assignee = "crew-a" });

        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        wrongState.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AssetTestData.ReadJsonAsync(stale))["currentVersion"]!.GetValue<long>().Should().Be(1);
        cancelled.StatusCode.Should().Be(HttpStatusCode.OK);
        reopen.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AssetTestData.CountAsync(_fixture, "work_task_history")).Should().Be(2);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /api/v1/tasks")]
    [Endpoint("GET /api/v1/tasks/{taskId}")]
    [Endpoint("GET /api/v1/tasks/{taskId}/history")]
    public async Task ReadEndpoints_ReturnTaskPermittedActionsAndHistory()
    {
        var taskId = await CreateRepairAsync();
        (await PostAsync($"{TasksPath}/{taskId}/transitions", Transition(WorkTaskAction.Assign, 1) with { Assignee = "crew-a" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var task = await _fixture.Client.GetFromJsonAsync($"{TasksPath}/{taskId}", AssetsJsonContext.Default.WorkTask);
        var list = await _fixture.Client.GetFromJsonAsync(
            $"{TasksPath}?serviceId={WebAppFixture.TestServiceId}&status=assigned",
            AssetsJsonContext.Default.WorkTaskListResult);
        var history = await _fixture.Client.GetFromJsonAsync($"{TasksPath}/{taskId}/history", AssetsJsonContext.Default.WorkTaskHistoryResult);
        var missing = await _fixture.Client.GetAsync($"{TasksPath}/{Guid.NewGuid()}/history");

        task!.Version.Should().Be(2);
        task.PermittedActions.Should().Contain(WorkTaskAction.Start);
        list!.Items.Should().ContainSingle(item => item.TaskId == taskId);
        history!.Items.Select(item => (item.Action, item.NewVersion)).Should().Equal(("create", 1L), ("assign", 2L));
        history.Items[1].ChangedFields.Should().Equal("status", "assignee");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<Guid> CreateRepairAsync()
    {
        var response = await PostAsync(TasksPath, RepairRequest());
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return Guid.Parse((await AssetTestData.ReadJsonAsync(response))["task"]!["taskId"]!.GetValue<string>());
    }

    private WorkTaskCreateRequest InspectionRequest(string assetId) => new()
    {
        CommandId = Guid.NewGuid(),
        TaskId = Guid.NewGuid(),
        ServiceId = WebAppFixture.TestServiceId,
        Type = WorkTaskType.Inspection,
        Title = "Inspect " + assetId,
        ProfileId = _profileId,
        AssetId = assetId,
        Assignee = "crew-a",
        DueAt = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private WorkTaskCreateRequest RepairRequest() => new()
    {
        CommandId = Guid.NewGuid(),
        TaskId = Guid.NewGuid(),
        ServiceId = WebAppFixture.TestServiceId,
        Type = WorkTaskType.Repair,
        Title = "Fallen branch on trail",
        Location = JsonElement.Parse("""{"type":"Point","coordinates":[-156.47,20.89]}"""),
        FormId = _form.FormId,
    };

    private static WorkTaskTransitionRequest Transition(string action, long expectedVersion)
        => new() { CommandId = Guid.NewGuid(), ExpectedVersion = expectedVersion, Action = action };

    private Task<HttpResponseMessage> PostAsync(string path, WorkTaskCreateRequest request)
        => _fixture.Client.PostAsJsonAsync(path, request, AssetsJsonContext.Default.WorkTaskCreateRequest);

    private Task<HttpResponseMessage> PostAsync(string path, WorkTaskTransitionRequest request)
        => _fixture.Client.PostAsJsonAsync(path, request, AssetsJsonContext.Default.WorkTaskTransitionRequest);

    private static async Task<IReadOnlyList<string>> FieldsAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        return [.. (await AssetTestData.ReadJsonAsync(response))["errors"]!.AsArray().Select(error => error!["field"]!.GetValue<string>())];
    }

}
