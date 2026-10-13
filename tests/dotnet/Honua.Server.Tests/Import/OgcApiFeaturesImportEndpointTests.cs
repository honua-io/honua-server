// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for OGC API Features collection import endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class OgcApiFeaturesImportEndpointTests : IAsyncLifetime
{
    private static readonly double[] InvalidBboxThreeValues = { 1.0, 2.0, 3.0 };

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/ogc-api-features/collection")]
    public async Task Import_UnownedTarget_Returns409AfterValidation()
    {
        var service = Substitute.For<IOgcApiFeaturesImportService>();
        service.ImportCollectionAsync(Arg.Any<OgcApiFeaturesImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OgcApiFeaturesImportResult
            {
                Success = false,
                CollectionId = "roads",
                Target = "honua_data.roads",
                ErrorCode = ImportTableOwnershipException.Code,
                ErrorMessage = ImportTableOwnershipException.CreateSafeMessage("honua_data.roads")
            });
        var fixture = new WebAppFixture().ReplaceService<IOgcApiFeaturesImportService>(service);
        await fixture.InitializeAsync();
        try
        {
            using var invalid = await fixture.Client.PostAsJsonAsync("/api/v1/admin/import/ogc-api-features/collection", new { });
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            await service.DidNotReceive().ImportCollectionAsync(Arg.Any<OgcApiFeaturesImportRequest>(), Arg.Any<CancellationToken>());

            using var response = await fixture.Client.PostAsJsonAsync("/api/v1/admin/import/ogc-api-features/collection", new { ServiceUrl = "https://example.com/ogcapi", CollectionId = "roads", TargetSchema = "honua_data" });
            var payload = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Conflict, payload);
            payload.Should().Contain(ImportTableOwnershipException.Code).And.Contain("honua_data.roads");
            await service.Received(1).ImportCollectionAsync(Arg.Any<OgcApiFeaturesImportRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/ogc-api-features/collection")]
    public async Task Collection_WithMissingServiceUrl_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/ogc-api-features/collection", new
        {
            CollectionId = "buildings",
            TargetSchema = "honua_data"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("ServiceUrl is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/ogc-api-features/collection")]
    public async Task Collection_WithEmptyFilter_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/ogc-api-features/collection", new
        {
            ServiceUrl = "https://example.com/ogcapi/",
            CollectionId = "buildings",
            TargetSchema = "honua_data",
            Filter = "   "
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Filter must be a non-empty");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/ogc-api-features/collection")]
    public async Task Collection_WithInvalidBboxLength_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/ogc-api-features/collection", new
        {
            ServiceUrl = "https://example.com/ogcapi/",
            CollectionId = "buildings",
            TargetSchema = "honua_data",
            Bbox = InvalidBboxThreeValues
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Bbox");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/ogc-api-features/collection")]
    public async Task Collection_WithInvalidDatetime_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/import/ogc-api-features/collection", new
        {
            ServiceUrl = "https://example.com/ogcapi/",
            CollectionId = "buildings",
            TargetSchema = "honua_data",
            Datetime = "   "
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Datetime");
    }
}
