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

// Shared setup for the asset and task tests: database reset and row counts.
internal static class AssetTestData
{
    // The database collection serializes these shared tables. Only fixture cleanup
    // suspends user triggers, within a transaction so failure restores every guard.
    public static async Task ResetAsync(WebAppFixture fixture)
    {
        string[] tables = ["asset_inspections", "asset_service_records", "work_task_history",
            "asset_command_receipts", "asset_summaries", "work_task_attachments", "work_tasks", "asset_profiles"];
        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema);
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var table in tables)
        {
            command.CommandText = $"ALTER TABLE honua.{table} DISABLE TRIGGER USER";
            await command.ExecuteNonQueryAsync();
        }

        command.CommandText = $"TRUNCATE {string.Join(", ", tables.Select(table => $"honua.{table}"))}";
        await command.ExecuteNonQueryAsync();
        foreach (var table in tables)
        {
            command.CommandText = $"ALTER TABLE honua.{table} ENABLE TRIGGER USER";
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
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

    public static async Task<WorkTaskFormReference> PublishFormAsync(WebAppFixture fixture, FormFieldDefinition[]? fields = null)
    {
        using var scope = fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFormPackageStore>();
        fields ??= [new FormFieldDefinition { FieldId = "name", Label = "Name", Type = "text", TargetField = "name", Required = true }];
        var draft = await store.SaveDraftAsync(
            new FormPackageDocument
            {
                Title = "Asset work " + Guid.NewGuid().ToString("N"),
                Target = new FormTargetDefinition { ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId },
                AttachmentPolicy = new FormAttachmentPolicy { Enabled = fields.Any(static field => field.Type is "attachment" or "media") },
