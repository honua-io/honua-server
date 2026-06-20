// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

/// <summary>
/// Regression coverage for honua-server#1911: WMS/WMTS GetCapabilities returned
/// HTTP 500 (a <c>ServiceExceptionReport</c> with <c>NoApplicableCode</c>) on
/// services whose time-configured layer stored temporal values as
/// epoch-milliseconds text (e.g. <c>1672617600000</c>, the encoding Esri
/// <c>applyEdits</c> writes) rather than ISO-8601. The capabilities builder
/// pre-fetches each time-aware layer's temporal extent with a MIN/MAX aggregate
/// whose <c>::timestamptz</c>/<c>::date</c> cast raised PostgreSQL SQLSTATE 22008
/// (<c>date/time field value out of range</c>) on the numeric text, failing the
/// whole document. These tests configure the shared test layer as time-aware,
/// write an epoch-ms value into its temporal column, and assert that both
/// GetCapabilities surfaces return 200 with a well-formed capabilities document.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class OgcClassicWmsCapabilitiesRobustnessTests : IAsyncLifetime
{
    // 2023-01-02T00:00:00Z expressed as epoch-milliseconds — the on-disk form
    // that raised SQLSTATE 22008 on a bare ::timestamptz cast in the issue.
    private const string EpochMillisTimestamp = "1672617600000";

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetCapabilities_TimeLayerWithEpochMillisValues_Returns200()
    {
        await ConfigureLayerAsTimeAwareAsync();
        await SetTemporalAttributeToEpochMillisAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().NotContain("ServiceExceptionReport");
        content.Should().Contain("<WMS_Capabilities");
        // The capabilities document still advertises the time-aware layer with a
        // resolved continuous time dimension (epoch-ms parsed as a real instant).
        content.Should().Contain("<Dimension name=\"time\" units=\"ISO8601\"");
    }

    [IntegrationTest]
    [Operation(Operations.Wmts)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetCapabilities_TimeLayerWithEpochMillisValues_Returns200()
    {
        await ConfigureLayerAsTimeAwareAsync();
        await SetTemporalAttributeToEpochMillisAsync();

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().NotContain("ExceptionReport");
        content.Should().Contain("<Capabilities");
        content.Should().Contain("<ows:Identifier>time</ows:Identifier>");
    }

    private Task ConfigureLayerAsTimeAwareAsync()
    {
        // Opt-in temporal config on the seeded "timestamp" DateTime field, mirroring
        // OgcClassicWmsTemporalTests; the resolver only advertises an extent when the
        // field resolves to a real Date/DateTime attribute.
        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            temporal: new MetadataV2ResourceTemporal { StartTimeField = "timestamp" });
        return Task.CompletedTask;
    }

    private async Task SetTemporalAttributeToEpochMillisAsync()
    {
        // Overwrite the seeded ISO-8601 "timestamp" attribute with an epoch-ms text
        // value to reproduce the Esri applyEdits on-disk encoding that 500'd the
        // capabilities builder.
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE features
            SET attributes = COALESCE(attributes, '{}'::jsonb)
                || jsonb_build_object('timestamp', @value::text)
            WHERE layer_id = @layerId;
            """;
        command.Parameters.Add(new NpgsqlParameter { ParameterName = "value", Value = EpochMillisTimestamp });
        command.Parameters.Add(new NpgsqlParameter { ParameterName = "layerId", Value = WebAppFixture.TestLayerId });
        await command.ExecuteNonQueryAsync();
    }
}
