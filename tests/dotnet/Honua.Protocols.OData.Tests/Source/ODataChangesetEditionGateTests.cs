// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Regression coverage for #1548: an OData <c>$batch</c> atomicity group (change set)
/// applies its writes directly through the shared writer, bypassing the per-request
/// <c>ODataCrudHandler</c> Pro edit gate. This proves a Community deployment cannot
/// create features by wrapping the write in a change set.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataChangesetEditionGateTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Community);

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task MultipartBatch_ChangesetWrite_UnderCommunity_ReturnsPaymentRequired()
    {
        const string batchBoundary = "batch_atomic";
        const string changesetBoundary = "changeset_1";
        var featureJson = """{"LayerId":0,"Attributes":{"name":"Atomic City"}}""";

        var payload = string.Join("\r\n",
        [
            $"--{batchBoundary}",
            $"Content-Type: multipart/mixed;boundary={changesetBoundary}",
            string.Empty,
            $"--{changesetBoundary}",
            "Content-Type: application/http",
            "Content-Transfer-Encoding: binary",
            string.Empty,
            "POST /odata/Features HTTP/1.1",
            "Content-Type: application/json",
            $"Content-Length: {Encoding.UTF8.GetByteCount(featureJson)}",
            string.Empty,
            featureJson,
            $"--{changesetBoundary}--",
            $"--{batchBoundary}--",
            string.Empty
        ]);

        var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed;boundary={batchBoundary}");

        var response = await _fixture.Client.PostAsync("/odata/$batch", content);

        // The outer batch is processed (200); the change-set part must be gated with 402
        // and must not create the feature (no 201).
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().Contain("402");
        responseBody.Should().NotContain("HTTP/1.1 201");
    }
}
