// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Admin;

public sealed partial class McpPublishedServicePostgresDurabilityTests
{
    [IntegrationTest]
    [Operation(Operations.Validation)]
    [Endpoint("POST /mcp")]
    public async Task PublicationPreview_ReportsTheSourceAndMetadataBase_WithoutPublishing()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var revision = await ReadPersistedRevisionAsync();
        var etag = await ReadPreviewMetadataEtagAsync();
        var sourceRows = await CountSourceRowsAsync(_firstTable);
        var sourceHash = await ReadPreviewSourceHashAsync();
        var result = await PreviewPublicationAsync();
        var handle = result.GetProperty("structuredContent");
        var content = result.GetProperty("content");
        content.GetArrayLength().Should().Be(1);
        content[0].GetProperty("type").GetString().Should().Be("text");
        using var text = JsonDocument.Parse(content[0].GetProperty("text").GetString()!);
        JsonElement.DeepEquals(text.RootElement, handle).Should().BeTrue();
        handle.GetProperty("status").GetString().Should().Be("Completed", handle.GetRawText());
        var details = handle.GetProperty("details");
        details.GetProperty("schema").GetString().Should().Be("public");
        details.GetProperty("table").GetString().Should().Be(_firstTable);
        details.GetProperty("serviceName").GetString().Should().Be(_serviceName);
        details.GetProperty("layerName").GetString().Should().Be("preview layer");
        details.GetProperty("metadataRevision").GetString().Should().Be(
            revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        details.GetProperty("metadataEtag").GetString().Should().Be(etag);
        details.GetProperty("previewReference").GetString().Should().NotBeNullOrWhiteSpace();
        DateTimeOffset.Parse(details.GetProperty("expiresAt").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture).Should().BeAfter(startedAt);
        AssertPlannedPublication(details);
        (await PublishedLayerCountAsync(_firstTable)).Should().Be(0);
        (await ReadPersistedRevisionAsync()).Should().Be(revision);
        (await ReadPreviewMetadataEtagAsync()).Should().Be(etag);
        (await CountSourceRowsAsync(_firstTable)).Should().Be(sourceRows);
        (await ReadPreviewSourceHashAsync()).Should().Be(sourceHash);
    }

    private void AssertPlannedPublication(JsonElement details)
    {
        details.GetProperty("layerIdentity").GetString().Should().Be("unallocated");
        details.GetProperty("routesAreAllocated").GetString().Should().Be("false");
        var service = JsonSerializer.Deserialize(details.GetProperty("plannedService").GetString()!,
            MetadataV2JsonContext.Default.MetadataV2Service)!;
        var resource = JsonSerializer.Deserialize(details.GetProperty("plannedResource").GetString()!,
            MetadataV2JsonContext.Default.MetadataV2Object)!;
        var binding = JsonSerializer.Deserialize(details.GetProperty("plannedStorageBinding").GetString()!,
            MetadataV2JsonContext.Default.MetadataV2StorageBinding)!;
        service.Metadata.Name.Should().Be(_serviceName);
        resource.SchemaFields.Select(field => field.Name).Should().Equal("id", "name");
        resource.StorageBindingIds.Should().Contain(binding.Metadata.Id);
        binding.ResourceId.Should().Be(resource.Metadata.Id);
        using var publications = JsonDocument.Parse(details.GetProperty("plannedPublications").GetString()!);
        publications.RootElement.GetArrayLength().Should().Be(2);
        foreach (var element in publications.RootElement.EnumerateArray())
        {
            var publication = element.Deserialize(MetadataV2JsonContext.Default.MetadataV2Layer)!;
            publication.ResourceId.Should().Be(resource.Metadata.Id);
            publication.ServiceId.Should().Be(service.Metadata.Id);
            publication.StorageBindingId.Should().Be(binding.Metadata.Id);
            service.PublicationIds.Should().Contain(publication.Metadata.Id);
        }
    }

    private async Task<string> ReadPreviewMetadataEtagAsync()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        return (await store.GetCurrentAsync()).Etag;
    }

    private async Task<string> ReadPreviewSourceHashAsync()
    {
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT md5(string_agg(row_to_json(source)::text, ',' ORDER BY id)) FROM public.{_firstTable} source";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<JsonElement> PreviewPublicationAsync(string[]? fields = null)
    {
        using var response = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "honua_op_service_publication_preview",
                arguments = new
                {
                    connectionId = _connectionId.ToString("D"),
                    schema = "public",
                    table = _firstTable,
                    layerName = "preview layer",
                    serviceName = _serviceName,
                    geometryColumn = "geom",
                    geometryType = "Point",
                    srid = 4326,
                    primaryKey = "id",
                    fields = fields ?? ["id", "name"]
                }
            }
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
        var result = document.RootElement.GetProperty("result");
        result.TryGetProperty("isError", out var isError);
        (isError.ValueKind == JsonValueKind.True).Should().BeFalse(body);
        result.GetProperty("content").GetArrayLength().Should().BeGreaterThan(0);
        return result.Clone();
    }
}
