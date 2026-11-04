// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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
public sealed partial class FolderSnapshotCompatibilityTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;

    public FolderSnapshotCompatibilityTests()
    {
        _fixture = new WebAppFixture();
        _fixture.ConfigureWebHost(b =>
        {
            b.UseEnvironment("Test");
            b.UseSetting("HONUA_DEV_AUTH", "false");
            b.UseSetting("MultiTenancy:DefaultTenantId", "");
            b.UseSetting("HONUA_ADMIN_PASSWORD", "folder-compatibility-key");
        }).ConfigureServices(services => FolderPostgresTestHost.Configure(services, () => _fixture.CurrentSchema));
    }

    private HttpClient _client = null!;
    private string Directory => Environment.GetEnvironmentVariable("HONUA_FOLDER_COMPAT_DIRECTORY")
        ?? throw new InvalidOperationException("The two-binary compatibility directory is required.");

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await FolderPostgresTestHost.InstallSchemaAsync(_fixture);
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", "folder-compatibility-key"));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [ExternalServiceTest("HONUA_FOLDER_COMPAT_DIRECTORY")]
    public async Task NewWriter_ProducesFolderSnapshotForBaselineReader()
    {
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
        var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        var snapshot = await store.GetCurrentAsync();
        var explicitLeaf = JsonNode.Parse(JsonSerializer.Serialize(snapshot.Graph, MetadataV2JsonContext.Default.MetadataV2Graph))!;
        foreach (var service in explicitLeaf["services"]!.AsArray().Where(service => service!["metadata"]!["name"]!.GetValue<string>() == "test"))
            service!["publicName"] = "test";
        var candidate = JsonSerializer.Deserialize(explicitLeaf.ToJsonString(), MetadataV2JsonContext.Default.MetadataV2Graph)!;
        snapshot = await store.SaveAsync(candidate, snapshot.Etag);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(snapshot.Graph, MetadataV2JsonContext.Default.MetadataV2Graph));
        serialized.RootElement.GetProperty("folders").GetArrayLength().Should().BeGreaterThan(0);
        await WriteSnapshot("new", snapshot);
        await RestartHost();
        await AssertFlatReads();
    }

    [ExternalServiceTest("HONUA_FOLDER_COMPAT_DIRECTORY")]
    public async Task NewReader_ReadsBaselineSaveAndStageAsRootServices()
    {
        foreach (var name in new[] { "old-save", "old-stage" })
        {
            var snapshot = await InstallSnapshot(name);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(snapshot.Graph, MetadataV2JsonContext.Default.MetadataV2Graph));
            document.RootElement.GetProperty("folders").GetArrayLength().Should().Be(0);
            document.RootElement.GetProperty("servicePathAliases").GetArrayLength().Should().Be(0);
            await RestartHost();
            await AssertFlatReads();
            await RestoreBackup();
            await RestartHost();
            await AssertFlatReads();
        }
    }

    private async Task<MetadataV2GraphSnapshot> InstallSnapshot(string name)
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        var current = await store.GetCurrentAsync();
        current = await store.SaveAsync(current.Graph, current.Etag);
        var document = JsonNode.Parse(await ReadSnapshotJson(name))!;
        document["revision"] = current.Revision;
        document["environment"] = current.Graph.Environment;
        var json = document.ToJsonString();
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

    private async Task<string> ReadSnapshotJson(string name)
    {
        var sourceSchema = await File.ReadAllTextAsync(Path.Join(Directory, name + ".schema"));
        return (await File.ReadAllTextAsync(Path.Join(Directory, name + ".json")))
            .Replace(sourceSchema, _fixture.CurrentSchema!, StringComparison.Ordinal);
    }

    private async Task RestoreBackup()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        var current = await store.GetCurrentAsync();
        var backup = JsonSerializer.Deserialize(await ReadSnapshotJson("new"), MetadataV2JsonContext.Default.MetadataV2Graph)!;
        var recovered = await store.SaveAsync(backup, current.Etag);
        recovered.Revision.Should().BeGreaterThan(current.Revision);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(recovered.Graph, MetadataV2JsonContext.Default.MetadataV2Graph));
        document.RootElement.GetProperty("folders").GetArrayLength().Should().BeGreaterThan(0);
        document.RootElement.GetProperty("servicePathAliases").GetArrayLength().Should().BeGreaterThan(0);
    }

    private async Task RestartHost()
    {
        _client.Dispose();
        await _fixture.RestartHostAsync();
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", "folder-compatibility-key"));
    }

    private async Task AssertFlatReads()
    {
        foreach (var path in new[] { "/rest/services?f=json", "/rest/services/test/FeatureServer?f=json", "/rest/services/test/FeatureServer/0/query?where=1%3D1&outFields=*&f=json" })
        {
            var response = await _client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.TryGetProperty("error", out _).Should().BeFalse();
            if (path.Contains("/query?", StringComparison.Ordinal))
                document.RootElement.GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
        }
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
