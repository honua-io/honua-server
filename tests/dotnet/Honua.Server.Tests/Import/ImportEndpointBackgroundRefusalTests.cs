// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Import;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Import;

/// <summary>
/// A host that cannot finish a background import (a Lambda host without a durable runner)
/// refuses it with the typed 503 before staging anything (honua-server#5916), while synchronous
/// imports under the threshold are unchanged.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class ImportEndpointBackgroundRefusalTests
{
    private const string GeoJson = """
        {
            "type": "FeatureCollection",
            "features": [
                {
                    "type": "Feature",
                    "geometry": { "type": "Point", "coordinates": [-122.4194, 37.7749] },
                    "properties": { "name": "Refused Point" }
                }
            ]
        }
        """;

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task ForcedBackgroundImport_OnHostWithoutRunner_ReturnsTyped503BeforeStaging()
    {
        var cloudStorage = Substitute.For<ICloudFileStorage>();
        cloudStorage.Provider.Returns(CloudStorageProvider.AwsS3);
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<ICloudFileStorage>();
            services.AddSingleton(cloudStorage);
            services.RemoveAll<IBackgroundImportRunnerInfo>();
            services.AddSingleton<IBackgroundImportRunnerInfo>(
                BackgroundImportRunnerDecision.Decide(false, null, frozenBetweenRequests: true));
        });

        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateClient();

            using var response = await client.PostAsync("/api/v1/admin/import/upload", CreateUpload("refused_table", forceBackground: true));

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("Background import service not available");
            await cloudStorage.DidNotReceiveWithAnyArgs().UploadAsync(default(FileUploadRequest)!, default);
            await cloudStorage.DidNotReceiveWithAnyArgs().UploadAsync(default(ByteArrayUploadRequest)!, default);

            using var manifest = await client.GetAsync("/api/v1/capabilities/manifest");
            manifest.StatusCode.Should().Be(HttpStatusCode.OK);
            var document = await manifest.Content.ReadFromJsonAsync<JsonElement>();
            var capability = document.GetProperty("capabilities").EnumerateArray()
                .Single(c => c.GetProperty("id").GetString() == "import.background");
            capability.GetProperty("runner").GetString().Should().Be("unavailable");
            capability.GetProperty("available").GetBoolean().Should().BeFalse();
            capability.GetProperty("reasonCode").GetString().Should().Be(BackgroundImportRunnerDecision.DurableRunnerRequiredReason);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static MultipartFormDataContent CreateUpload(string tableName, bool forceBackground)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new StringContent(GeoJson, Encoding.UTF8, "application/json");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "refused.geojson",
        };
        content.Add(fileContent);
        content.Add(new StringContent(tableName), "TableName");
        content.Add(new StringContent(forceBackground ? "true" : "false"), "ForceBackground");
        return content;
    }
}
