// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class StudioMapStyleMcpToolTests : IAsyncLifetime
{
    private const string MapSpec = """
        {"version":8,"sources":{"parcels":{"type":"geojson","data":{}}},
         "layers":[{"id":"parcels","type":"fill","source":"parcels"}]}
        """;

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureServices(services =>
        {
            services.RemoveAll<IStudioPackageStore>();
            services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
        });

    private StudioMcpHttpClient _mcp = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _mcp = new StudioMcpHttpClient(_fixture.Client);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp")]
    [Endpoint("GET /api/v1/studio/package-drafts/{draftId}")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task SetMapStyle_AfterToolCall_SurvivesADraftSyncReadBack()
    {
        var (draftId, generation) = await CreateStyledMapDraftAsync();
        var restyled = MapSpec.Replace("\"source\":\"parcels\"", "\"source\":\"parcels\",\"paint\":{\"fill-color\":\"#112233\"}", StringComparison.Ordinal);

        await _mcp.CallToolAsync(
            "honua_studio_set_map_style",
            "{\"draftId\":\"" + draftId + "\",\"generation\":" + generation + ",\"mapSpec\":" + restyled + "}");

        var body = (await _mcp.SyncEnvelopeAsync(draftId)).Body!.Value;
        body.GetProperty("mapSpec").GetProperty("layers")[0].GetProperty("paint").GetProperty("fill-color").GetString()
            .Should().Be("#112233");
        body.GetProperty("styleRefs").GetArrayLength().Should().Be(0);
        body.GetProperty("layers")[0].TryGetProperty("styleRef", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task SetView_WithABearingOutOfBounds_SurfacesAnOutOfBoundsViolationWithoutWriting()
    {
        var (draftId, generation) = await CreateStyledMapDraftAsync();

        var refused = await _mcp.CallToolRawAsync(
            "honua_studio_set_view",
            "{\"draftId\":\"" + draftId + "\",\"generation\":" + generation + ",\"view\":{\"bearing\":360}}");

        refused.GetProperty("isError").GetBoolean().Should().BeTrue();
        var error = refused.GetProperty("structuredContent");
        error.GetProperty("message").GetString().Should().Be("setView: bearing 360 is out of bounds [0, 360)");
        error.GetProperty("violations")[0].GetProperty("code").GetString().Should().Be("out-of-bounds");
        (await _mcp.SyncEnvelopeAsync(draftId)).Body!.Value.TryGetProperty("view", out _).Should().BeFalse();
    }

    private async Task<(Guid DraftId, long Generation)> CreateStyledMapDraftAsync()
    {
        var created = await _mcp.CallToolAsync(
            "honua_studio_create_draft",
            """{"packageKey":"parcels-style-map","family":"map","schemaVersion":"1.0"}""");
        var draftId = created.GetProperty("draftId").GetGuid();
        var body = """
            {"mapPackageId":"parcels-style-map","format":"honua_map_package.v1",
             "layers":[{"id":"parcels","sourceId":"parcels","visible":true,"styleRef":"honua-inline:parcels"}],
             "styleRefs":[{"styleId":"honua-inline:parcels","body":{"parcels":{"paint":{}}}}],
             "mapSpec":
            """ + MapSpec + "}";

        var seeded = await _mcp.CallToolAsync(
            "honua_studio_update_draft",
            "{\"draftId\":\"" + draftId + "\",\"generation\":" + created.GetProperty("generation").GetInt64()
            + ",\"packageKey\":\"parcels-style-map\",\"schemaVersion\":\"1.0\",\"body\":" + body + "}");
        return (draftId, seeded.GetProperty("generation").GetInt64());
    }
}
