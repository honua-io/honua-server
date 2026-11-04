// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class FolderSnapshotCompatibilityTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().UseSeed("tests/seed/server.yaml")
        .ConfigureWebHost(b =>
        {
            b.UseEnvironment("Test");
            b.UseSetting("HONUA_DEV_AUTH", "false");
            b.UseSetting("HONUA_ADMIN_PASSWORD", "folder-compatibility-key");
        });
    private HttpClient _client = null!;
    private string? Directory => Environment.GetEnvironmentVariable("HONUA_FOLDER_COMPAT_DIRECTORY");

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", "folder-compatibility-key"));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    public async Task NewWriter_ProducesFolderSnapshotForBaselineReader()
    {
        if (Directory is null) return;
        var listing = await _client.GetAsync("/api/v1/admin/folders");
        listing.EnsureSuccessStatusCode();
        _client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", listing.Headers.ETag!.ToString());
        var created = await _client.PostAsJsonAsync("/api/v1/admin/folders", new { name = "Survey" });
        created.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("data").GetProperty("metadata").GetProperty("id").GetString();
        _client.DefaultRequestHeaders.Remove("If-Match");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", created.Headers.ETag!.ToString());
        (await _client.PutAsJsonAsync("/api/v1/admin/services/test/folder", new { folderId = id })).EnsureSuccessStatusCode();
        using var scope = _fixture.Services.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>().GetCurrentAsync();
        await WriteSnapshot("new", snapshot);
        await AssertFlatReads();
    }

    [IntegrationTest]
    public async Task BaselineReader_ReadsNewSnapshotAndProvesSaveStageFieldLoss()
    {
        if (Directory is null) return;
        var snapshot = await InstallSnapshot("new");
        var serialized = JsonSerializer.Serialize(snapshot.Graph, MetadataV2JsonContext.Default.MetadataV2Graph);
        serialized.Should().NotContain("\"folders\"");
        await AssertFlatReads();
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        var staged = await ((IMetadataV2GraphRevisionStager)store).StageAsync(snapshot.Graph);
        await WriteSnapshot("old-stage", staged);
        var saved = await store.SaveAsync(snapshot.Graph, snapshot.Etag);
        await WriteSnapshot("old-save", saved);
        (await store.GetCurrentAsync()).Etag.Should().Be(saved.Etag);
    }

    [IntegrationTest]
    public async Task NewReader_ReadsBaselineSaveAndStageAsRootServices()
    {
        if (Directory is null) return;
        foreach (var name in new[] { "old-save", "old-stage" })
        {
            var snapshot = await InstallSnapshot(name);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(snapshot.Graph, MetadataV2JsonContext.Default.MetadataV2Graph));
            document.RootElement.GetProperty("folders").GetArrayLength().Should().Be(0);
            document.RootElement.GetProperty("servicePathAliases").GetArrayLength().Should().Be(0);
            await AssertFlatReads();
        }
    }

    private async Task<MetadataV2GraphSnapshot> InstallSnapshot(string name)
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        var current = await store.GetCurrentAsync();
        current = await store.SaveAsync(current.Graph, current.Etag);
        var sourceSchema = await File.ReadAllTextAsync(Path.Join(Directory!, name + ".schema"));
        var json = (await File.ReadAllTextAsync(Path.Join(Directory!, name + ".json")))
            .Replace(sourceSchema, _fixture.CurrentSchema!, StringComparison.Ordinal);
        await using var connection = await scope.ServiceProvider.GetRequiredService<IAdoNetDatabaseConnectionProvider>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE "{_fixture.CurrentSchema}".metadata_v2_snapshots
            SET document = @document::jsonb, etag = @etag
            WHERE environment = @environment AND revision = @revision;
            UPDATE "{_fixture.CurrentSchema}".metadata_v2_current SET etag = @etag WHERE environment = @environment;
            """;
        AddParameter(command, "document", json);
        AddParameter(command, "etag", "\"compatibility-input\"");
        AddParameter(command, "environment", current.Graph.Environment);
        AddParameter(command, "revision", current.Revision);
        (await command.ExecuteNonQueryAsync()).Should().Be(2);
        scope.ServiceProvider.GetRequiredService<IMetadataV2GraphCacheInvalidator>().Invalidate(current.Graph.Environment);
        return await store.GetCurrentAsync();
    }

    private async Task AssertFlatReads()
    {
        foreach (var path in new[] { "/rest/services", "/rest/services/test/FeatureServer", "/rest/services/test/FeatureServer/0/query?where=1%3D1&outFields=*&f=json" })
            (await _client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task WriteSnapshot(string name, MetadataV2GraphSnapshot snapshot)
    {
        await File.WriteAllTextAsync(Path.Join(Directory!, name + ".json"),
            JsonSerializer.Serialize(snapshot.Graph, MetadataV2JsonContext.Default.MetadataV2Graph));
        await File.WriteAllTextAsync(Path.Join(Directory!, name + ".schema"), _fixture.CurrentSchema);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
