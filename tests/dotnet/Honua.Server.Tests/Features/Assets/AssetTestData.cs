// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Forms.Packages;
using Honua.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Assets;

// Shared setup for the asset and task tests: database reset and counts, fixture assets, a published form and a profile.
internal static class AssetTestData
{
    // TRUNCATE bypasses the append-only row triggers, which is what a test reset needs.
    public static async Task ResetAsync(WebAppFixture fixture)
    {
        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            TRUNCATE honua.asset_inspections, honua.asset_service_records, honua.work_task_history,
                honua.asset_command_receipts, honua.asset_summaries, honua.work_tasks, honua.asset_profiles
            """;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task ExecuteAsync(WebAppFixture fixture, string sql)
    {
        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(WebAppFixture fixture, string sql)
    {
        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    // The fixture layer's seed differs from base-schema, so tests read real object IDs and coordinates from the layer.
    public static async Task<IReadOnlyList<(string AssetId, double X)>> DiscoverAssetsAsync(WebAppFixture fixture)
    {
        var body = await fixture.Client.GetStringAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1%3D1&outFields=*&returnGeometry=true&resultRecordCount=20&f=json");
        var assets = new List<(string AssetId, double X)>();
        foreach (var feature in System.Text.Json.Nodes.JsonNode.Parse(body)!["features"]!.AsArray())
        {
            var objectId = feature?["attributes"]?.AsObject()
                .FirstOrDefault(pair => string.Equals(pair.Key, "objectid", StringComparison.OrdinalIgnoreCase)).Value;
            if (objectId is not null && feature!["geometry"]?["x"] is { } x)
            {
                assets.Add((objectId.ToString(), x.GetValue<double>()));
            }
        }

        assets.Should().HaveCountGreaterThanOrEqualTo(3, body[..Math.Min(body.Length, 2000)]);
        return assets;
    }

    public static async Task<WorkTaskFormReference> PublishFormAsync(WebAppFixture fixture)
    {
        using var scope = fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFormPackageStore>();
        var draft = await store.SaveDraftAsync(
            new FormPackageDocument
            {
                Title = "Asset work " + Guid.NewGuid().ToString("N"),
                Target = new FormTargetDefinition { ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId },
                Sections = [new FormSectionDefinition { SectionId = "main", Label = "Main", FieldIds = ["name"] }],
            },
            "test-admin");
        var published = await store.PublishAsync(draft.FormId, draft.Version, new FormPackageValidationResult { IsValid = true }, "test-admin")
            ?? throw new InvalidOperationException("The test form did not publish.");
        return new WorkTaskFormReference { FormId = published.FormId, Version = published.Version };
    }

    // An active profile over the fixture layer keyed by objectid, with the form bound for inspections and service work.
    public static async Task<Guid> CreateActiveProfileAsync(HttpClient client, WorkTaskFormReference form)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/assets/profiles",
            new AssetProfileWriteRequest
            {
                CommandId = Guid.NewGuid(),
                ServiceId = WebAppFixture.TestServiceId,
                LayerId = WebAppFixture.TestLayerId,
                Name = "Signs",
                Fields = new AssetProfileFieldMap { AssetId = "objectid" },
                ConditionLabels = ["Very poor", "Poor", "Fair", "Good", "Very good"],
                InspectionFormId = form.FormId,
                ServiceFormId = form.FormId,
                State = AssetProfileState.Active,
            },
            AssetsJsonContext.Default.AssetProfileWriteRequest);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return Guid.Parse((await ReadJsonAsync(response))["profile"]!["profileId"]!.GetValue<string>());
    }

    public static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    public static async Task<long> CountAsync(WebAppFixture fixture, string table)
    {
        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM honua.{table}";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
