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
public sealed class WorkTaskCompletionTests : IAsyncLifetime
{
    private const string TasksPath = "/api/v1/tasks";

    private static readonly DateTimeOffset Earlier = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

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
        foreach (var table in new[] { "asset_summaries", "work_task_history", "asset_command_receipts" })
        {
            await AssetTestData.ExecuteAsync(_fixture, $"DROP TRIGGER IF EXISTS fail_completion_write ON honua.{table}");
        }

        await AssetTestData.ResetAsync(_fixture);
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_Inspection_WritesRecordAndSummaryOnceAcrossReplays()
    {
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        var request = Completion(expectedVersion: 3, Later, condition: 2);

        var completed = await CompleteAsync(taskId, request);
        var replay = await CompleteAsync(taskId, request);

        completed.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await AssetTestData.ReadJsonAsync(completed);
        body["task"]!["status"]!.GetValue<string>().Should().Be(WorkTaskStatus.Completed);
        body["inspectionId"].Should().NotBeNull();
        (await AssetTestData.ReadJsonAsync(replay))["outcome"]!.GetValue<string>().Should().Be(AssetCommandOutcome.Duplicate);
        (await AssetTestData.CountAsync(_fixture, "asset_inspections")).Should().Be(1);
        (await SummaryAsync(_assets[0].AssetId)).Should().Be((2, 1L));
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_OlderObservationArrivesLate_DoesNotRegressTheSummary()
    {
        var newer = await StartedInspectionAsync(_assets[1].AssetId);
        var older = await StartedInspectionAsync(_assets[1].AssetId);

        (await CompleteAsync(newer, Completion(3, Later, condition: 4))).StatusCode.Should().Be(HttpStatusCode.OK);
        var late = await CompleteAsync(older, Completion(3, Earlier, condition: 1) with { SummaryRevision = 0 });

        late.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AssetTestData.CountAsync(_fixture, "asset_inspections")).Should().Be(2);
        (await SummaryAsync(_assets[1].AssetId)).Should().Be((4, 1L));
    }

    // The record insert comes first; a failure at each later write must leave none of the completion behind.
    [IntegrationTheory]
    [InlineData("asset_summaries", "")]
    [InlineData("work_task_history", "WHEN (NEW.action = 'complete')")]
    [InlineData("asset_command_receipts", "WHEN (NEW.action = 'complete')")]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_LaterWriteFails_RollsBackEveryRecord(string table, string when)
    {
        var taskId = await StartedInspectionAsync(_assets[2].AssetId);
        await AssetTestData.ExecuteAsync(_fixture, $"""
            CREATE OR REPLACE FUNCTION honua.fail_completion_write() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'injected completion failure'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_completion_write BEFORE INSERT OR UPDATE ON honua.{table}
                FOR EACH ROW {when} EXECUTE FUNCTION honua.fail_completion_write();
            """);

        var failed = await CompleteAsync(taskId, Completion(3, Later, condition: 3));

        failed.IsSuccessStatusCode.Should().BeFalse();
        (await failed.Content.ReadAsStringAsync()).Should().NotContain("injected");
        (await AssetTestData.CountAsync(_fixture, "asset_inspections")).Should().Be(0);
        (await AssetTestData.CountAsync(_fixture, "asset_summaries")).Should().Be(0);
        (await AssetTestData.CountAsync(_fixture, "work_task_history")).Should().Be(3);
        (await AssetTestData.ScalarAsync(_fixture, $"SELECT status FROM honua.work_tasks WHERE task_id = '{taskId}'"))
            .Should().Be(WorkTaskStatus.InProgress);
        (await AssetTestData.CountAsync(_fixture, "asset_command_receipts")).Should().Be(4, "the profile and three task commands");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_ServiceAfterInspection_AdvancesItsOwnWatermarkOnly()
    {
        var inspection = await StartedInspectionAsync(_assets[0].AssetId);
        (await CompleteAsync(inspection, Completion(3, Later, condition: 4))).StatusCode.Should().Be(HttpStatusCode.OK);
        var maintenance = await StartedTaskAsync(_assets[0].AssetId, WorkTaskType.Maintenance);

        var serviced = await CompleteAsync(maintenance, Completion(3, Earlier, condition: null));

        serviced.StatusCode.Should().Be(HttpStatusCode.OK, await serviced.Content.ReadAsStringAsync());
        (await AssetTestData.ReadJsonAsync(serviced))["serviceRecordId"].Should().NotBeNull();
        (await AssetTestData.CountAsync(_fixture, "asset_service_records")).Should().Be(1);
        (await SummaryAsync(_assets[0].AssetId)).Should().Be((4, 2L), "the service group advanced; the inspection group kept its newer value");
        (await AssetTestData.ScalarAsync(_fixture, $"SELECT last_serviced_at = '{Earlier:O}'::timestamptz FROM honua.asset_summaries WHERE asset_id = '{_assets[0].AssetId}'"))
            .Should().Be(true);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_InvalidCompletions_AreRejectedWithoutWrites()
    {
        var notStarted = await CreateInspectionAsync(_assets[0].AssetId);
        var started = await StartedInspectionAsync(_assets[0].AssetId);

        var fromAssigned = await CompleteAsync(notStarted, Completion(1, Later, condition: 3));
        var wrongForm = await CompleteAsync(started, Completion(3, Later, condition: 3) with { Form = _form with { Version = _form.Version + 1 } });
        var future = await CompleteAsync(started, Completion(3, DateTimeOffset.UtcNow.AddDays(1), condition: 3));
        var outOfRange = await CompleteAsync(started, Completion(3, Later, condition: 6));

        fromAssigned.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        wrongForm.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        future.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        outOfRange.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AssetTestData.CountAsync(_fixture, "asset_inspections")).Should().Be(0);
    }

    private Task<Guid> StartedInspectionAsync(string assetId) => StartedTaskAsync(assetId, WorkTaskType.Inspection);

    private async Task<Guid> StartedTaskAsync(string assetId, string type)
    {
        var taskId = await CreateTaskAsync(assetId, type);
        var started = await _fixture.Client.PostAsJsonAsync(
            $"{TasksPath}/{taskId}/transitions",
            new WorkTaskTransitionRequest { CommandId = Guid.NewGuid(), ExpectedVersion = 1, Action = WorkTaskAction.Start },
            AssetsJsonContext.Default.WorkTaskTransitionRequest);
        started.StatusCode.Should().Be(HttpStatusCode.OK, await started.Content.ReadAsStringAsync());

        // A reassignment gives every started task the same version arithmetic: create 1, start 2, reassign 3.
        var reassigned = await _fixture.Client.PostAsJsonAsync(
            $"{TasksPath}/{taskId}/transitions",
            new WorkTaskTransitionRequest { CommandId = Guid.NewGuid(), ExpectedVersion = 2, Action = WorkTaskAction.Reassign, Assignee = "crew-b" },
            AssetsJsonContext.Default.WorkTaskTransitionRequest);
        reassigned.StatusCode.Should().Be(HttpStatusCode.OK, await reassigned.Content.ReadAsStringAsync());
        return taskId;
    }

    private Task<Guid> CreateInspectionAsync(string assetId) => CreateTaskAsync(assetId, WorkTaskType.Inspection);

    private async Task<Guid> CreateTaskAsync(string assetId, string type)
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            TasksPath,
            new WorkTaskCreateRequest
            {
                CommandId = Guid.NewGuid(),
                TaskId = Guid.NewGuid(),
                ServiceId = WebAppFixture.TestServiceId,
                Type = type,
                Title = type + " " + assetId,
                ProfileId = _profileId,
                AssetId = assetId,
                Assignee = "crew-a",
            },
            AssetsJsonContext.Default.WorkTaskCreateRequest);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return Guid.Parse((await AssetTestData.ReadJsonAsync(response))["task"]!["taskId"]!.GetValue<string>());
    }

    private WorkTaskCompletionRequest Completion(long expectedVersion, DateTimeOffset observedAt, int? condition) => new()
    {
        CommandId = Guid.NewGuid(),
        ExpectedVersion = expectedVersion,
        ObservedAt = observedAt,
        Form = _form,
        Condition = condition,
        Answers = JsonElement.Parse("""{"faceLegible":true}"""),
        Notes = "Post leaning slightly.",
    };

    private Task<HttpResponseMessage> CompleteAsync(Guid taskId, WorkTaskCompletionRequest request)
        => _fixture.Client.PostAsJsonAsync($"{TasksPath}/{taskId}/completion", request, AssetsJsonContext.Default.WorkTaskCompletionRequest);

    private async Task<(int Condition, long Revision)> SummaryAsync(string assetId)
    {
        var condition = await AssetTestData.ScalarAsync(_fixture, $"SELECT condition FROM honua.asset_summaries WHERE asset_id = '{assetId}'");
        var revision = await AssetTestData.ScalarAsync(_fixture, $"SELECT revision FROM honua.asset_summaries WHERE asset_id = '{assetId}'");
        return (Convert.ToInt32(condition, System.Globalization.CultureInfo.InvariantCulture), (long)revision!);
    }
}
