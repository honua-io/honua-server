// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Verifies OData reaches parity with the GeoServices f=parquet surface by emitting
/// GeoParquet through the shared cloud-native writer for <c>$format=parquet</c> (issue #1621).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataParquetFormatTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;
    private const string ParquetContentType = "application/vnd.apache.parquet";

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$format=parquet")]
    public async Task Features_FormatParquet_ReturnsGeoParquetPayload()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$format=parquet");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(ParquetContentType);

        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();

        // Parquet files start and end with the "PAR1" magic bytes.
        Encoding.ASCII.GetString(payload, 0, 4).Should().Be("PAR1");
        Encoding.ASCII.GetString(payload, payload.Length - 4, 4).Should().Be("PAR1");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$format=parquet&$filter=...")]
    public async Task Features_FormatParquetWithFilter_ReturnsFilteredGeoParquetPayload()
    {
        var filter = "state in ('California','Washington')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$format=parquet&$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(ParquetContentType);

        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
        Encoding.ASCII.GetString(payload, 0, 4).Should().Be("PAR1");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$format=parquet&bbox=...")]
    public async Task Features_FormatParquetWithBbox_ReturnsGeoParquetPayload()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$format=parquet&bbox=-124,32,-114,42");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(ParquetContentType);

        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
        Encoding.ASCII.GetString(payload, 0, 4).Should().Be("PAR1");
    }
}
