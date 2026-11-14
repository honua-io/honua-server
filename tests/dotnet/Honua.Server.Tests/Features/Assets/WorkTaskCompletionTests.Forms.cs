// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Forms.Packages;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class WorkTaskCompletionTests
{
    [IntegrationTheory]
    [InlineData("{}")]
    [InlineData("{\"name\":null}")]
    [InlineData("{\"name\":42}")]
    [InlineData("{\"name\":\"unlisted\"}")]
    [InlineData("{\"name\":\"inspection\",\"extra\":true}")]
    [InlineData("{\"name\":\"inspection\",\"name\":\"inspection\"}")]
    [InlineData("{\"name\":\"inspection\",\"NAME\":\"inspection\"}")]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_InvalidPinnedFormAnswers_LeavesTaskAndEvidenceUnchanged(string answers)
    {
        await UseRequiredCompletionFormAsync();
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        var request = Completion(3, Later, 3) with { Answers = JsonElement.Parse(answers) };
        var receipts = await AssetTestData.CountAsync(_fixture, "asset_command_receipts");
        var history = await AssetTestData.CountAsync(_fixture, "work_task_history");

        var response = await CompleteAsync(taskId, request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await AssetTestData.CountAsync(_fixture, "asset_inspections")).Should().Be(0);
        (await AssetTestData.CountAsync(_fixture, "asset_summaries")).Should().Be(0);
        (await AssetTestData.CountAsync(_fixture, "asset_command_receipts")).Should().Be(receipts);
        (await AssetTestData.CountAsync(_fixture, "work_task_history")).Should().Be(history);
        (await AssetTestData.ScalarAsync(_fixture, $"SELECT version FROM honua.work_tasks WHERE task_id = '{taskId}'")).Should().Be(3L);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_ValidPinnedFormAnswers_PreservesAnswersInEvidence()
    {
        await UseRequiredCompletionFormAsync();
        var taskId = await StartedInspectionAsync(_assets[0].AssetId);
        var request = Completion(3, Later, 3) with { Answers = JsonElement.Parse("{\"name\":\"inspection\"}") };

        var response = await CompleteAsync(taskId, request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await AssetTestData.ScalarAsync(_fixture, "SELECT answers->>'name' FROM honua.asset_inspections")).Should().Be("inspection");
    }

    private async Task UseRequiredCompletionFormAsync()
    {
        _form = await AssetTestData.PublishFormAsync(_fixture,
        [new FormFieldDefinition
        {
            FieldId = "name", Label = "Name", Type = "choice", TargetField = "name", SectionId = "main", Required = true,
            Domain = new FormFieldDomainDefinition
            {
                Type = "codedValue", Choices = [new FormDomainChoice { Code = JsonElement.Parse("\"inspection\""), Label = "Inspection" }]
            }
        }]);
        await AssetTestData.ExecuteAsync(_fixture,
            $"UPDATE honua.asset_profiles SET inspection_form_id = '{_form.FormId}', service_form_id = '{_form.FormId}', version = version + 1 WHERE profile_id = '{_profileId}'");
    }
}
