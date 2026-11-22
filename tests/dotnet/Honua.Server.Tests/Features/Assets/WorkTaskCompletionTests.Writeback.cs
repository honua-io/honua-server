// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class WorkTaskCompletionTests
{
    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_MappedInspection_UpdatesFeatureOnlyForNewerObservation()
    {
        await MapInspectionFieldsAsync();
        var assetId = _assets[0].AssetId;
        var newer = await StartedInspectionAsync(assetId);
        var older = await StartedInspectionAsync(assetId);

        var request = Completion(3, Later, 4);
        var accepted = await CompleteAsync(newer, request);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        (await CompleteAsync(newer, request)).StatusCode.Should().Be(HttpStatusCode.OK);
        var oldRequest = Completion(3, Earlier, 1);
        var late = await CompleteAsync(older, oldRequest);
        late.StatusCode.Should().Be(HttpStatusCode.OK, await late.Content.ReadAsStringAsync());

        (await OutboxCountAsync(request.CommandId!.Value)).Should().Be(1L);
        (await OutboxCountAsync(oldRequest.CommandId!.Value)).Should().Be(0L);
        var body = await _fixture.Client.GetStringAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?objectIds={assetId}&outFields=population,timestamp&returnGeometry=false&f=json");
        using var document = JsonDocument.Parse(body);
        var attributes = document.RootElement.GetProperty("features")[0].GetProperty("attributes");
        attributes.GetProperty("population").GetInt32().Should().Be(4);
        attributes.GetProperty("timestamp").GetInt64().Should().Be(Later.ToUnixTimeMilliseconds());
        (await SummaryAsync(assetId)).Should().Be((4, 1L));
    }

    [IntegrationTheory]
    [InlineData("asset_inspections", "")]
    [InlineData("asset_summaries", "")]
    [InlineData("features", "")]
    [InlineData("work_tasks", "WHEN (NEW.status = 'completed')")]
    [InlineData("work_task_history", "WHEN (NEW.action = 'complete')")]
    [InlineData("feature_change_outbox", "")]
    [InlineData("asset_command_receipts", "WHEN (NEW.action = 'complete')")]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_MappedWriteFails_RollsBackFeatureAndOutbox(string table, string when)
    {
        await MapInspectionFieldsAsync();
        var assetId = _assets[0].AssetId;
        var taskId = await StartedInspectionAsync(assetId);
        var before = await ReadMappedFeatureAsync(assetId);
        var request = Completion(3, Later, 3);
        await AssetTestData.ExecuteAsync(_fixture, $"""
            CREATE OR REPLACE FUNCTION honua.fail_completion_write() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'injected completion failure'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_completion_write BEFORE INSERT OR UPDATE ON {CompletionFaultTable(table)}
                FOR EACH ROW {when} EXECUTE FUNCTION honua.fail_completion_write();
            """);

        var failed = await CompleteAsync(taskId, request);

        if (table is "features" or "feature_change_outbox")
        {
            failed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await failed.Content.ReadAsStringAsync()).Should().Contain("The mapped asset changed or the update was refused");
        }
        else
        {
            failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError, await failed.Content.ReadAsStringAsync());
        }
        await AssertMappedRollbackAsync(taskId, (assetId, before), request.CommandId!.Value);

        await AssetTestData.ExecuteAsync(_fixture, $"DROP TRIGGER fail_completion_write ON {CompletionFaultTable(table)}");
        var retry = await CompleteAsync(taskId, request);
        retry.StatusCode.Should().Be(HttpStatusCode.OK, await retry.Content.ReadAsStringAsync());
        (await OutboxCountAsync(request.CommandId!.Value)).Should().Be(1L);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_ReadOnlyViewRelation_PreservesFeatureAndAdvertisesSummaryOnly()
    {
        await MapInspectionFieldsAsync();
        var assetId = _assets[0].AssetId;
        var taskId = await StartedInspectionAsync(assetId);
        var before = await ReadStoredMappedFeatureAsync(assetId);
        await AssetTestData.ExecuteAsync(_fixture,
            $"CREATE VIEW \"{_fixture.CurrentSchema}\".summary_assets AS SELECT * FROM \"{_fixture.CurrentSchema}\".features");
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var service = snapshot.FindServiceByNameOrId(WebAppFixture.TestServiceId)!;
        var publication = snapshot.FindPublicationByLayerIndex(service.Metadata.Id, WebAppFixture.TestLayerId)!;
        var binding = snapshot.ResolveStorageBinding(publication)!;
        var options = binding.Options is null
            ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>(binding.Options);
        options["tableName"] = JsonSerializer.SerializeToElement("summary_assets");
        var graph = snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            StorageBindings = snapshot.Graph.StorageBindings.Select(item => item.Metadata.Id == binding.Metadata.Id
                ? item with { Options = options } : item).ToArray(),
        };
        ((TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>()).SetGraph(graph, schema: _fixture.CurrentSchema);
        var request = Completion(3, Later, 4);

        var accepted = await CompleteAsync(taskId, request);

        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        (await SummaryAsync(assetId)).Should().Be((4, 1L));
        (await ReadStoredMappedFeatureAsync(assetId)).Should().Be(before);
        (await OutboxCountAsync(request.CommandId!.Value)).Should().Be(0L);
        using var profile = JsonDocument.Parse(await _fixture.Client.GetStringAsync($"/api/v1/assets/profiles/{_profileId}"));
        profile.RootElement.GetProperty("completionCapabilities").GetProperty("mappedAssetWriteback").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_MappedResourceBecomesReadOnly_DoesNotWriteCompletion()
    {
        await MapInspectionFieldsAsync();
        var assetId = _assets[0].AssetId;
        var taskId = await StartedInspectionAsync(assetId);
        var before = await ReadMappedFeatureAsync(assetId);
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var service = snapshot.FindServiceByNameOrId(WebAppFixture.TestServiceId)!;
        var publication = snapshot.FindPublicationByLayerIndex(service.Metadata.Id, WebAppFixture.TestLayerId)!;
        var graph = snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Resources = snapshot.Graph.Resources.Select(item => item.Metadata.Id == publication.ResourceId
                ? item with { Relationships = [new MetadataV2Relationship { Id = "owned", Composite = true }] } : item).ToArray(),
        };
        ((TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>()).SetGraph(graph, schema: _fixture.CurrentSchema);
        var request = Completion(3, Later, 4);

        var refused = await CompleteAsync(taskId, request);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync());
        (await refused.Content.ReadAsStringAsync()).Should().Contain("The mapped asset update is not permitted.");
        await AssertMappedRollbackAsync(taskId, (assetId, before), request.CommandId!.Value);
    }

    [IntegrationTheory]
    [InlineData("schemaName", "other_assets")]
    [InlineData("geometryColumn", "other_geometry")]
    [InlineData("tableName", "FEATURES")]
    [Operation(Operations.GetById)]
    [Endpoint("GET /api/v1/assets/profiles/{profileId}")]
    public async Task GetProfile_NonManagedMapping_DoesNotAdvertiseMappedWriteback(string option, string value)
    {
        await MapInspectionFieldsAsync();
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var service = snapshot.FindServiceByNameOrId(WebAppFixture.TestServiceId)!;
        var publication = snapshot.FindPublicationByLayerIndex(service.Metadata.Id, WebAppFixture.TestLayerId)!;
        var binding = snapshot.ResolveStorageBinding(publication)!;
        var options = binding.Options is null
            ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>(binding.Options);
        options[option] = JsonSerializer.SerializeToElement(value);
        var graph = snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            StorageBindings = snapshot.Graph.StorageBindings.Select(item => item.Metadata.Id == binding.Metadata.Id
                ? item with { Options = options } : item).ToArray(),
        };
        ((TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>()).SetGraph(graph, schema: _fixture.CurrentSchema);

        using var profile = JsonDocument.Parse(await _fixture.Client.GetStringAsync($"/api/v1/assets/profiles/{_profileId}"));

        profile.RootElement.GetProperty("completionCapabilities").GetProperty("mappedAssetWriteback").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/tasks/{taskId}/completion")]
    public async Task Complete_ReboundLayerDoesNotContainAsset_DoesNotReadOldManagedTable()
    {
        await MapInspectionFieldsAsync();
        var assetId = _assets[0].AssetId;
        var taskId = await StartedInspectionAsync(assetId);
        await AssetTestData.ExecuteAsync(_fixture,
            $"CREATE TABLE \"{_fixture.CurrentSchema}\".rebound_assets (LIKE \"{_fixture.CurrentSchema}\".features INCLUDING ALL)");
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var service = snapshot.FindServiceByNameOrId(WebAppFixture.TestServiceId)!;
        var publication = snapshot.FindPublicationByLayerIndex(service.Metadata.Id, WebAppFixture.TestLayerId)!;
        var binding = snapshot.ResolveStorageBinding(publication)!;
        var options = new Dictionary<string, JsonElement>(binding.Options!);
        options["tableName"] = JsonSerializer.SerializeToElement("rebound_assets");
        var graph = snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            StorageBindings = snapshot.Graph.StorageBindings.Select(item => item.Metadata.Id == binding.Metadata.Id
                ? item with { Options = options } : item).ToArray(),
        };
        ((TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>()).SetGraph(graph, schema: _fixture.CurrentSchema);

        var refused = await CompleteAsync(taskId, Completion(3, Later, 4));

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync());
        (await refused.Content.ReadAsStringAsync()).Should().Contain("No asset with this ID exists on the layer.");
        (await AssetTestData.CountAsync(_fixture, "asset_inspections")).Should().Be(0L);
        (await AssetTestData.CountAsync(_fixture, "asset_summaries")).Should().Be(0L);
    }

    private async Task AssertMappedRollbackAsync(Guid taskId, (string AssetId, string Before) feature, Guid commandId)
    {
        (await AssetTestData.CountAsync(_fixture, "asset_inspections")).Should().Be(0L);
        (await AssetTestData.CountAsync(_fixture, "asset_summaries")).Should().Be(0L);
        (await OutboxCountAsync(commandId)).Should().Be(0L);
        (await ReadMappedFeatureAsync(feature.AssetId)).Should().Be(feature.Before);
        (await AssetTestData.ScalarAsync(_fixture, $"SELECT status FROM honua.work_tasks WHERE task_id = '{taskId}'"))
            .Should().Be(WorkTaskStatus.InProgress);
        (await AssetTestData.ScalarAsync(_fixture,
            $"SELECT count(*) FROM honua.work_task_history WHERE task_id = '{taskId}' AND action = 'complete'"))
            .Should().Be(0L);
        (await AssetTestData.ScalarAsync(_fixture,
            $"SELECT count(*) FROM honua.asset_command_receipts WHERE command_id = '{commandId}'"))
            .Should().Be(0L);
    }

    private string CompletionFaultTable(string table)
        => table == "features" ? $"\"{_fixture.CurrentSchema}\".features" : $"honua.{table}";

    private Task<object?> OutboxCountAsync(Guid commandId)
        => AssetTestData.ScalarAsync(_fixture, $"SELECT count(*) FROM honua.feature_change_outbox WHERE request_id = '{commandId:D}'");

    private Task<object?> ReadStoredMappedFeatureAsync(string assetId)
        => AssetTestData.ScalarAsync(_fixture,
            $"SELECT attributes::text FROM \"{_fixture.CurrentSchema}\".features WHERE layer_id = {WebAppFixture.TestLayerId} AND objectid = {long.Parse(assetId, System.Globalization.CultureInfo.InvariantCulture)}");

    private Task<string> ReadMappedFeatureAsync(string assetId)
        => _fixture.Client.GetStringAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?objectIds={assetId}&outFields=population,timestamp&returnGeometry=false&f=json");

    private async Task MapInspectionFieldsAsync()
    {
        var mapped = await _fixture.Client.PutAsJsonAsync(
            $"/api/v1/assets/profiles/{_profileId}",
            new AssetProfileWriteRequest
            {
                CommandId = Guid.NewGuid(), ExpectedVersion = 1,
                ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId,
                Name = "Signs", State = AssetProfileState.Active,
                Fields = new AssetProfileFieldMap { AssetId = "objectid", Condition = "population", LastInspected = "timestamp" },
                ConditionLabels = ["Very poor", "Poor", "Fair", "Good", "Very good"],
                InspectionFormId = _form.FormId, ServiceFormId = _form.FormId,
            }, AssetsJsonContext.Default.AssetProfileWriteRequest);
        mapped.StatusCode.Should().Be(HttpStatusCode.OK, await mapped.Content.ReadAsStringAsync());
    }
}
