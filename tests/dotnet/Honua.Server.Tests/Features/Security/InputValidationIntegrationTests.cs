// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Features.Security;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class InputValidationIntegrationTests : IAsyncLifetime
{
    private const string AdminPassword = "Valid-Test-Key123!";
    private readonly WebAppFixture _fixture;

    public InputValidationIntegrationTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment(Environments.Production);
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("HONUA_DEV_AUTH", "false");
            });
    }

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithSqlInjectionPattern_ReturnsBadRequest()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/rest/services/test/FeatureServer/1/query?where=1%3D1%20UNION%20SELECT%20*%20FROM%20pg_user");
        request.Headers.Add("X-API-Key", AdminPassword);

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("SQL injection attempt detected");
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithSqlKeywordInsideStringLiteral_IsAllowed()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/rest/services/test/FeatureServer/1/query?where=name%20%3D%20%27select%20cafe%27&f=json");
        request.Headers.Add("X-API-Key", AdminPassword);

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithNestedWhereExpression_IsAllowed()
    {
        var where = Uri.EscapeDataString("((name = 'a' OR name = 'b') AND objectid > 0) OR category = 'test'");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/test/FeatureServer/0/query?where={where}&f=json");
        request.Headers.Add("X-API-Key", AdminPassword);

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithPathTraversalPatternInQueryParameter_ReturnsBadRequest()
    {
        var where = Uri.EscapeDataString("../etc/passwd");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/test/FeatureServer/1/query?where={where}&f=json");
        request.Headers.Add("X-API-Key", AdminPassword);

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Path traversal attempt detected");
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task FeatureServer_WithSqlLikeBearerCredential_DoesNotReturnBadRequest()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/rest/services/test/FeatureServer");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer opaque--token");

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithSqlKeywordInsideOpaqueCredentialHeader_IsNotRejectedByInputValidation()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/rest/services/test/FeatureServer/1/query?where=1%3D1&f=json");
        request.Headers.Add("X-API-Key", AdminPassword);
        request.Headers.Add("Authorization", "Bearer opaque-select-token-from-ci");

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
    }
}

[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
[Operation(Operations.Security)]
public sealed class InputValidationODataIntegrationTests : IAsyncLifetime
{
    private const string AdminPassword = "Valid-Test-Key123!";
    private readonly WebAppFixture _fixture;

    public InputValidationODataIntegrationTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/odata.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment(Environments.Production);
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("HONUA_DEV_AUTH", "false");
            });
    }

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /odata/Features({layerId})?$expand=Landmarks($select=name)&$filter=ObjectId eq 1")]
    public async Task ODataExpand_WithNestedQueryOptionSyntax_IsAllowed()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/odata/Features(0)?$expand=Landmarks($select=name)&$filter=ObjectId eq 1");
        request.Headers.Add("X-API-Key", AdminPassword);

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /odata/Features({layerId})/$apply?$apply=groupby((state), aggregate(...));DROP TABLE")]
    public async Task ODataApply_WithSqlInjectionPayload_StillReturnsBadRequest()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/odata/Features(0)/$apply?$apply=groupby((state), aggregate(population with sum as TotalPop));DROP TABLE users");
        request.Headers.Add("X-API-Key", AdminPassword);

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("SQL injection attempt detected");
        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetProperty("code").GetString().Should().Be("BadRequest");
    }
}
