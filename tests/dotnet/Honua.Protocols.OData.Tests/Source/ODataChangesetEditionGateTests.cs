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
/// Regression coverage for #1591: an OData <c>$batch</c> atomicity group (change set)
/// applies writes through the shared writer without an edition gate. This proves a
/// Community deployment can edit through the open OData surface.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataChangesetEditionGateTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Community);

    public async Task InitializeAsync()
    {
        // All segments are relative literal path fragments (not user input), so none can be
        // rooted and silently drop earlier arguments.
        _fixture.UseSeed(Path.Join("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task MultipartBatch_ChangesetWrite_UnderCommunity_CreatesFeature()
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

        using var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed;boundary={batchBoundary}");

        var response = await _fixture.Client.PostAsync("/odata/$batch", content);

        // The outer batch is processed (200); the change-set write is Community and
        // should reach the shared edit pipeline instead of short-circuiting with 402.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync();
        // Assert the absence of the 402 status LINE, not a bare "402" substring: the
        // multipart batch response boundary is a random GUID (e.g.
        // "--batchresponse_e95a2abc2ef8402cb8c0...") that can incidentally contain
        // "402" and flake an over-broad substring check.
        responseBody.Should().NotContain("HTTP/1.1 402");
        responseBody.Should().Contain("HTTP/1.1 201");
    }
}
