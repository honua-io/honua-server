// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net.Http.Headers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.Server.Tests.Features.Security;

// Attachment mutations must refuse callers without a data-editor grant on the owning service
// before any form, feature or attachment work happens.
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerAttachmentRbacTests : IClassFixture<FeatureServerAttachmentRbacTests.DeniedRequestFixture>
{
    private const long FeatureId = 1;
    private const long AttachmentId = 1;

    private readonly DeniedRequestFixture _fixture;

    public FeatureServerAttachmentRbacTests(DeniedRequestFixture fixture) => _fixture = fixture;

    // Every case here is a denial, so one shared host cannot be mutated by the suite.
    public sealed class DeniedRequestFixture : IDisposable
    {
        public WebApplicationFactory<Program> Application { get; } = ServiceRbacTestFixture.CreateFactory();

        public void Dispose() => Application.Dispose();
    }

    public static TheoryData<string, string, int> DeniedCallers => new()
    {
        // caller roles ("" = anonymous), target service, expected GeoServices error code
        { string.Empty, ServiceRbacTestFixture.AlphaService, 401 },
        { "reader", ServiceRbacTestFixture.AlphaService, 403 },
        { $"data-editor:{ServiceRbacTestFixture.AlphaService}", ServiceRbacTestFixture.BetaService, 403 },
    };

    [IntegrationTheory]
    [Operation(Operations.AddAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    [MemberData(nameof(DeniedCallers))]
    public async Task AddAttachment_WithoutDataEditorGrant_IsRefused(string roles, string serviceId, int expectedCode)
    {
        using var client = CreateCaller(roles);
        using var form = CreateFileForm();

        var response = await client.PostAsync(FeatureRoute(serviceId, "addAttachment"), form);

        await AssertRefusedAsync(response, expectedCode);
    }

    [IntegrationTheory]
    [Operation(Operations.UpdateAttachment)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/updateAttachment")]
    [MemberData(nameof(DeniedCallers))]
    public async Task UpdateAttachment_WithoutDataEditorGrant_IsRefused(string roles, string serviceId, int expectedCode)
    {
        using var client = CreateCaller(roles);
        using var form = CreateFileForm();
        form.Add(new StringContent(AttachmentId.ToString(CultureInfo.InvariantCulture)), "attachmentId");

        var response = await client.PostAsync(FeatureRoute(serviceId, "updateAttachment"), form);

        await AssertRefusedAsync(response, expectedCode);
    }

    [IntegrationTheory]
    [Operation(Operations.DeleteAttachments)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
    [MemberData(nameof(DeniedCallers))]
    public async Task DeleteAttachments_WithoutDataEditorGrant_IsRefused(string roles, string serviceId, int expectedCode)
    {
        using var client = CreateCaller(roles);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(AttachmentId.ToString(CultureInfo.InvariantCulture)), "attachmentIds" },
            { new StringContent("json"), "f" },
        };

        var response = await client.PostAsync(FeatureRoute(serviceId, "deleteAttachments"), form);

        await AssertRefusedAsync(response, expectedCode);
    }

    private HttpClient CreateCaller(string roles) => roles.Length == 0
        ? _fixture.Application.CreateClient()
        : ServiceRbacTestFixture.CreateClient(_fixture.Application, roles);

    private static string FeatureRoute(string serviceId, string operation)
    {
        var layerId = serviceId == ServiceRbacTestFixture.AlphaService
            ? ServiceRbacTestFixture.AlphaLayerId
            : ServiceRbacTestFixture.BetaLayerId;
        return $"/rest/services/{serviceId}/FeatureServer/{layerId}/{FeatureId}/{operation}";
    }

    private static MultipartFormDataContent CreateFileForm()
    {
        var file = new ByteArrayContent("rbac"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        return new MultipartFormDataContent
        {
            { file, "attachment", "rbac.txt" },
            { new StringContent("json"), "f" },
        };
    }

    private static Task AssertRefusedAsync(HttpResponseMessage response, int expectedCode) =>
        expectedCode == 401
            ? response.AssertGeoServicesErrorAsync(401, 499)
            : response.AssertGeoServicesErrorAsync(expectedCode);
}
