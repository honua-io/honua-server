// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Verifies that <c>GET /api/v1/admin/capabilities</c> emits the canonical
/// <c>data.compatibility</c> envelope the generated admin SDKs handshake on, per
/// docs/developer/SDK_COMPATIBILITY_METADATA.md (controlPlaneApi / releaseChannel /
/// metadataSchemas / features) — not just the flat fields.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AdminCapabilitiesEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/capabilities")]
    public async Task GetCapabilities_EmitsDocumentedCompatibilityEnvelope()
    {
        var response = await _fixture.Client.GetAsync("/api/v1/admin/capabilities");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        var compatibility = document.RootElement.GetProperty("data").GetProperty("compatibility");

        compatibility.GetProperty("serverVersion").GetString().Should().NotBeNullOrWhiteSpace();
        compatibility.GetProperty("releaseChannel").GetString().Should().NotBeNullOrWhiteSpace();

        // controlPlaneApi.major must be the numeric major the SDK gates on first.
        var controlPlaneApi = compatibility.GetProperty("controlPlaneApi");
        controlPlaneApi.GetProperty("major").GetInt32().Should().Be(1);
        controlPlaneApi.GetProperty("basePath").GetString().Should().Be("/api/v1/admin");
        controlPlaneApi.GetProperty("deprecated").GetBoolean().Should().BeFalse();

        // metadataSchemas is an array of { version, deprecated }, newest non-deprecated usable.
        var metadataSchemas = compatibility.GetProperty("metadataSchemas");
        metadataSchemas.ValueKind.Should().Be(JsonValueKind.Array);
        metadataSchemas.GetArrayLength().Should().BeGreaterThan(0);
        metadataSchemas[0].GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        metadataSchemas[0].TryGetProperty("deprecated", out _).Should().BeTrue();

        // features advertises the manifest workflow switches.
        var features = compatibility.GetProperty("features");
        features.GetProperty("metadataResources").GetBoolean().Should().BeTrue();
        features.GetProperty("manifestExport").GetBoolean().Should().BeTrue();
        features.GetProperty("manifestApply").GetBoolean().Should().BeTrue();
        features.GetProperty("manifestDryRun").GetBoolean().Should().BeTrue();
        features.GetProperty("manifestPrune").GetBoolean().Should().BeTrue();
    }
}
