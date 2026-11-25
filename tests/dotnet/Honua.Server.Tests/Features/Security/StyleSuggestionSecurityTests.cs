// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Security;

[SecurityTest]
public sealed class StyleSuggestionSecurityTests(StyleSuggestionSecurityTests.StyleHost host)
    : IClassFixture<StyleSuggestionSecurityTests.StyleHost>
{
    private const string GetRendererEndpoint = "GET /rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer";
    private const string PostRendererEndpoint = "POST /rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer";
    private const string SuggestStyleEndpoint = "POST /api/v1/admin/metadata/layers/{storageLayerId}/suggest-style";
    private const string AlphaRenderer = "/rest/services/alpha/FeatureServer/0/generateRenderer";
    private const string BetaRenderer = "/rest/services/beta/FeatureServer/1/generateRenderer";
    private const string SuggestStyle = "/api/v1/admin/metadata/layers/0/suggest-style";
    private const int MaxParameterLength = 256;
    private const int MaxPayloadSize = 1024 * 1024;

    public sealed class StyleHost : IDisposable
    {
        public WebApplicationFactory<Program> Application { get; } = ServiceRbacTestFixture
            .CreateFactory(() => new RbacTestLayerCatalog(
                betaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["beta-reader"]),
                alphaLayerMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["alpha-reader"])))
            .WithWebHostBuilder(builder => builder
                .UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword)
                .UseSetting("InputValidation:MaxParameterLength", MaxParameterLength.ToString(CultureInfo.InvariantCulture))
                .UseSetting("Limits:Edits:MaxPayloadSize", MaxPayloadSize.ToString(CultureInfo.InvariantCulture)));

        public void Dispose() => Application.Dispose();
    }

    [IntegrationTheory]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Security)]
    [Endpoint(GetRendererEndpoint)]
    [Endpoint(PostRendererEndpoint)]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task GenerateRenderer_Anonymous_RequiresAuthentication(string method)
    {
        using var client = host.Application.CreateClient();
        using var response = await SendRendererAsync(client, method, AlphaRenderer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(499, error.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [IntegrationTheory]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Security)]
    [Endpoint(GetRendererEndpoint)]
    [Endpoint(PostRendererEndpoint)]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task GenerateRenderer_WithoutLayerReadRole_ReturnsForbidden(string method)
    {
        using var client = ServiceRbacTestFixture.CreateClient(host.Application, "reader");
        using var response = await SendRendererAsync(client, method, AlphaRenderer);

        await response.AssertGeoServicesErrorAsync(403);
    }

    [IntegrationTheory]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Security)]
    [Endpoint(GetRendererEndpoint)]
    [Endpoint(PostRendererEndpoint)]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task GenerateRenderer_ServiceScopeIsolation_OnlyReturnsGrantedRenderer(string method)
    {
        using var client = ServiceRbacTestFixture.CreateClient(host.Application, "alpha-reader");
        using var allowed = await SendRendererAsync(client, method, AlphaRenderer);
        using var denied = await SendRendererAsync(client, method, BetaRenderer);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var renderer = JsonDocument.Parse(await allowed.Content.ReadAsStringAsync());
        Assert.Equal("simple", renderer.RootElement.GetProperty("type").GetString());
        await denied.AssertGeoServicesErrorAsync(403);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Security)]
    [Endpoint(GetRendererEndpoint)]
    public async Task GenerateRenderer_UnknownQueryParameter_ReturnsBadRequest()
    {
        using var client = ServiceRbacTestFixture.CreateClient(host.Application, "alpha-reader");
        using var response = await client.GetAsync(AlphaRenderer + "?unexpected=true");

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Security)]
    [Endpoint(PostRendererEndpoint)]
    public async Task GenerateRenderer_UnknownJsonProperty_ReturnsBadRequest()
    {
        using var client = ServiceRbacTestFixture.CreateClient(host.Application, "alpha-reader");
        using var body = JsonContent("""{"unexpected":true}""");
        using var response = await client.PostAsync(AlphaRenderer, body);

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTheory]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Security)]
    [Endpoint(GetRendererEndpoint)]
    [Endpoint(PostRendererEndpoint)]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task GenerateRenderer_LayerOutsideService_ReturnsNotFound(string method)
    {
        using var client = ServiceRbacTestFixture.CreateClient(host.Application, "alpha-reader", "beta-reader");
        using var response = await SendRendererAsync(client, method, "/rest/services/alpha/FeatureServer/1/generateRenderer");

        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Security)]
    [Endpoint(GetRendererEndpoint)]
    public async Task GenerateRenderer_QueryParameterExceedingLimit_ReturnsBadRequest()
    {
        using var client = ServiceRbacTestFixture.CreateClient(host.Application, "alpha-reader");
        var classification = Uri.EscapeDataString("{}" + new string(' ', MaxParameterLength - 1));
        using var response = await client.GetAsync(AlphaRenderer + "?classificationDef=" + classification);

        await response.AssertGeoServicesErrorAsync(400);
        Assert.Contains("exceeds maximum length", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Security)]
    [Endpoint(PostRendererEndpoint)]
    public async Task GenerateRenderer_MalformedJsonBody_ReturnsBadRequest()
    {
        using var client = ServiceRbacTestFixture.CreateClient(host.Application, "alpha-reader");
        using var body = JsonContent("{");
        using var response = await client.PostAsync(AlphaRenderer, body);

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Security)]
    [Endpoint(PostRendererEndpoint)]
    public async Task GenerateRenderer_NonObjectJsonBody_ReturnsBadRequest()
    {
        using var client = ServiceRbacTestFixture.CreateClient(host.Application, "alpha-reader");
        using var body = JsonContent("[]");
        using var response = await client.PostAsync(AlphaRenderer, body);

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Security)]
    [Endpoint(PostRendererEndpoint)]
    public async Task GenerateRenderer_UnsupportedMediaType_Returns415()
    {
        using var client = ServiceRbacTestFixture.CreateClient(host.Application, "alpha-reader");
        using var body = new StringContent("{}", Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync(AlphaRenderer, body);

        await response.AssertGeoServicesErrorAsync(415);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Security)]
    [Endpoint(PostRendererEndpoint)]
    public async Task GenerateRenderer_BodyExceedingLimit_Returns413()
    {
        using var client = ServiceRbacTestFixture.CreateClient(host.Application, "alpha-reader");
        using var body = OversizedJsonContent();
        using var response = await client.PostAsync(AlphaRenderer, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(413, error.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Admin)]
    [Operation(Operations.Security)]
    [Endpoint(SuggestStyleEndpoint)]
    public async Task SuggestStyle_Anonymous_Returns401()
    {
        using var client = host.Application.CreateClient();
        using var body = JsonContent("{}");
        using var response = await client.PostAsync(SuggestStyle, body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [IntegrationTheory]
    [Protocol(TestProtocols.Admin)]
    [Operation(Operations.Security)]
    [Endpoint(SuggestStyleEndpoint)]
    [InlineData("admin:read")]
    [InlineData("read:alpha")]
    public async Task SuggestStyle_WithoutAdminWriteScope_Returns403(string permission)
    {
        var keys = host.Application.Services.GetRequiredService<IAdminApiKeyStore>();
        var created = await keys.CreateAsync("style-scope-test", [permission],
            DateTimeOffset.UtcNow.AddMinutes(10), "test", CancellationToken.None);
        using var client = host.Application.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", created.Key);
        using var body = JsonContent("{}");
        using var response = await client.PostAsync(SuggestStyle, body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [IntegrationTheory]
    [Protocol(TestProtocols.Admin)]
    [Operation(Operations.Security)]
    [Endpoint(SuggestStyleEndpoint)]
    [InlineData("{")]
    [InlineData("""{"classCount":"three"}""")]
    public async Task SuggestStyle_InvalidJsonBody_ReturnsBadRequest(string json)
    {
        using var client = CreateBootstrapClient();
        using var body = JsonContent(json);
        using var response = await client.PostAsync(SuggestStyle, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Admin)]
    [Operation(Operations.Security)]
    [Endpoint(SuggestStyleEndpoint)]
    public async Task SuggestStyle_BodyExceedingLimit_Returns413()
    {
        using var client = CreateBootstrapClient();
        using var body = OversizedJsonContent();
        using var response = await client.PostAsync(SuggestStyle, body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(413, error.RootElement.GetProperty("status").GetInt32());
    }

    private HttpClient CreateBootstrapClient()
    {
        var client = host.Application.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword);
        return client;
    }

    private static async Task<HttpResponseMessage> SendRendererAsync(HttpClient client, string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (request.Method == HttpMethod.Post)
        {
            request.Content = JsonContent("{}");
        }

        return await client.SendAsync(request);
    }

    private static StringContent OversizedJsonContent() => JsonContent("{}" + new string(' ', MaxPayloadSize - 1));

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");
}
