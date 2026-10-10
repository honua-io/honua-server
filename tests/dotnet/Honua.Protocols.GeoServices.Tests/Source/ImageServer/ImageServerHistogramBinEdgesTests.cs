// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerHistogramBinEdgesTests
{
    private static readonly double[] SourceCenters = [200, 120, 60];
    private static readonly string[] SoapOperations = ["GetServiceInfo", "ComputeHistograms"];

    [IntegrationTheory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 4096)]
    [InlineData(true, 4096)]
    [Operation(Operations.Metadata)]
    [InterfaceOperation(TestProtocols.ImageServer, "GetServiceInfo")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    [Endpoint("GET /rest/services/{id}/ImageServer/histograms")]
    [Endpoint("GET /rest/services/{id}/ImageServer/{rasterId}/info/histograms")]
    [Endpoint("POST /rest/services/{id}/ImageServer/computeStatisticsHistograms")]
    public async Task ConstantHistograms_SoapAndRest_PreserveSqlCentersCountsAndNoData(bool mosaic, int noDataPixels)
    {
        var fixture = new WebAppFixture();
        await fixture.InitializeAsync();
        try
        {
            await fixture.Postgres.RunUnderSchemaMutationLockAsync(async () =>
            {
                await SeedAsync(fixture, mosaic, noDataPixels);
                var oracle = await ReadSqlOracleAsync(fixture);
                oracle.Should().HaveCount(3);
                for (var band = 0; band < oracle.Length; band++)
                {
                    oracle[band].Count.Should().Be(4096 - noDataPixels);
                    oracle[band].Sum.Should().Be(SourceCenters[band] * oracle[band].Count);
                }

                var root = $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer";
                using (var response = await fixture.Client.GetAsync(root + "/histograms?f=json"))
                {
                    AssertRestHistograms(await ReadJsonAsync(response), oracle);
                }

                using (var form = new FormUrlEncodedContent([new KeyValuePair<string, string>("f", "json")]))
                using (var response = await fixture.Client.PostAsync(root + "/computeStatisticsHistograms", form))
                {
                    AssertRestHistograms(await ReadJsonAsync(response), oracle);
                }

                await using (var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema!))
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT id FROM honua.raster_data WHERE layer_id = @layerId ORDER BY id";
                    command.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
                    var ids = new List<long>();
                    await using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync()) { ids.Add(reader.GetInt64(0)); }
                    }
                    foreach (var id in ids)
                    {
                        var itemOracle = await ReadSqlOracleAsync(fixture, id);
                        using var response = await fixture.Client.GetAsync(root + $"/{id}/info/histograms?f=json");
                        AssertRestHistograms(await ReadJsonAsync(response), itemOracle);
                    }
                }

                foreach (var operation in SoapOperations)
                {
                    using var content = new StringContent($"""
                        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                          <soap:Body><{operation} xmlns="http://www.esri.com/schemas/ArcGIS/10.8" /></soap:Body>
                        </soap:Envelope>
                        """, Encoding.UTF8, "text/xml");
                    using var response = await fixture.Client.PostAsync(
                        $"/services/{WebAppFixture.TestServiceId}/ImageServer", content);
                    var body = await response.Content.ReadAsStringAsync();
                    response.StatusCode.Should().Be(HttpStatusCode.OK, body);
                    var xml = XDocument.Parse(body);
                    xml.Descendants().Where(element => element.Name.LocalName == "Fault").Should().BeEmpty(body);
                    var histograms = xml.Descendants().Where(element => element.Name.LocalName == "RasterHistogram").ToArray();
                    histograms.Should().HaveCount(3, body);
                    for (var band = 0; band < histograms.Length; band++)
                    {
                        var histogram = histograms[band];
                        var counts = histogram.Descendants().Where(element => element.Name.LocalName == "Double")
                            .Select(element => long.Parse(element.Value, CultureInfo.InvariantCulture)).ToArray();
                        AssertHistogram(int.Parse(Child(histogram, "Size"), CultureInfo.InvariantCulture),
                            double.Parse(Child(histogram, "Min"), CultureInfo.InvariantCulture),
                            double.Parse(Child(histogram, "Max"), CultureInfo.InvariantCulture), counts, oracle[band]);
                    }
                }
            });
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{id}/ImageServer/histograms")]
    public async Task EmptyLayer_Histograms_PreservesNotFoundEnvelope()
    {
        var fixture = new WebAppFixture();
        await fixture.InitializeAsync();
        try
        {
            await fixture.Postgres.RunUnderSchemaMutationLockAsync(async () =>
            {
                await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema!);
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM honua.raster_data WHERE layer_id = @layerId";
                command.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
                await command.ExecuteNonQueryAsync();
                using var response = await fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/histograms?f=json");
                var body = await response.Content.ReadAsStringAsync();
                response.StatusCode.Should().Be(HttpStatusCode.OK, body);
                using var json = JsonDocument.Parse(body);
                json.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404, body);
                json.RootElement.TryGetProperty("histograms", out _).Should().BeFalse(body);
            });
        }
        finally { await fixture.DisposeAsync(); }
    }

    [UnitTheory]
    [InlineData(-200d)]
    [InlineData(0d)]
    [InlineData(200d)]
    [InlineData(1e20)]
    [InlineData(double.MaxValue / 2)]
    [InlineData(-double.MaxValue / 2)]
    public void ConstantFloatingHistogram_EdgesAreFinitePositiveAndCentered(double value)
    {
        var result = ImageServerHistogramProjection.Project(new RasterHistogram
        {
            Band = 1,
            BinCount = 1,
            Min = value,
            Max = value,
            Counts = [4096],
        });
        double.IsFinite(result.Min).Should().BeTrue();
        double.IsFinite(result.Max).Should().BeTrue();
        result.Max.Should().BeGreaterThan(result.Min);
        (result.Min / 2 + result.Max / 2).Should().BeApproximately(value, Math.Max(1e-12, Math.Abs(value) * 1e-15));
        result.Counts.Should().Equal(4096);
    }

    [UnitTest]
    public void NonconstantHistogram_PreservesEdgesAndCounts()
    {
        var histogram = new RasterHistogram { Band = 1, BinCount = 3, Min = -0.5, Max = 2.5, Counts = [7, 0, 11] };
        var result = ImageServerHistogramProjection.Project(histogram);
        result.Size.Should().Be(histogram.BinCount);
        result.Min.Should().Be(histogram.Min);
        result.Max.Should().Be(histogram.Max);
        result.Counts.Should().BeSameAs(histogram.Counts);
    }

    [UnitTheory]
    [InlineData(0)]
    [InlineData(1)]
    public void EmptyOrZeroCountHistogram_IsNotWidened(int size)
    {
        var histogram = new RasterHistogram { Band = 1, BinCount = size, Min = 0, Max = 0, Counts = size == 0 ? [] : [0] };
        var result = ImageServerHistogramProjection.Project(histogram);
        result.Size.Should().Be(size);
        result.Min.Should().Be(0);
        result.Max.Should().Be(0);
        result.Counts.Should().BeSameAs(histogram.Counts);
    }

    [UnitTheory]
    [InlineData(double.MaxValue)]
    [InlineData(-double.MaxValue)]
    public void ConstantAtFiniteLimit_RejectsUnrepresentableCenteredEdges(double value)
    {
        var histogram = new RasterHistogram { Band = 1, BinCount = 1, Min = value, Max = value, Counts = [4096] };
        var project = () => ImageServerHistogramProjection.Project(histogram);
        project.Should().Throw<InvalidDataException>().WithMessage("*finite bin edges*");
    }

    private static void AssertRestHistograms(JsonElement json, SqlBand[] oracle)
    {
        var histograms = json.GetProperty("histograms");
        histograms.GetArrayLength().Should().Be(oracle.Length);
        for (var band = 0; band < oracle.Length; band++)
        {
            var histogram = histograms[band];
            AssertHistogram(histogram.GetProperty("size").GetInt32(), histogram.GetProperty("min").GetDouble(),
                histogram.GetProperty("max").GetDouble(),
                histogram.GetProperty("counts").EnumerateArray().Select(value => value.GetInt64()).ToArray(), oracle[band]);
        }
    }

    private static void AssertHistogram(int size, double min, double max, long[] counts, SqlBand oracle)
    {
        counts.LongLength.Should().Be(size);
        counts.Sum().Should().Be(oracle.Count);
        if (oracle.Count == 0)
        {
            size.Should().Be(0, "NoData pixels must not become a populated constant bin");
            return;
        }
        size.Should().Be(1);
        double.IsFinite(min).Should().BeTrue();
        double.IsFinite(max).Should().BeTrue();
        (max - min).Should().Be(1);
        var center = min / 2 + max / 2;
        center.Should().Be(oracle.Center);
        (center * counts[0]).Should().Be(oracle.Sum);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
        return json.RootElement.Clone();
    }

    private static string Child(XElement element, string name)
        => element.Elements().Single(child => child.Name.LocalName == name).Value;

    private static async Task<SqlBand[]> ReadSqlOracleAsync(WebAppFixture fixture, long? rasterId = null)
    {
        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH merged AS (
                SELECT ST_Union(raster ORDER BY created_at, id) rast FROM honua.raster_data
                WHERE layer_id = @layerId AND (@rasterId = 0 OR id = @rasterId)
            )
            SELECT band, (s).count, (s).sum, (s).min, (s).max
            FROM merged CROSS JOIN generate_series(1,3) band
            CROSS JOIN LATERAL ST_SummaryStats(rast, band, true) s
            ORDER BY band
            """;
        command.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
        command.Parameters.AddWithValue("rasterId", rasterId ?? 0);
        var results = new List<SqlBand>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var count = reader.GetInt64(1);
            if (count > 0) { reader.GetDouble(3).Should().Be(reader.GetDouble(4)); }
            results.Add(new SqlBand(count, reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                reader.IsDBNull(3) ? 0 : reader.GetDouble(3)));
        }
        return results.ToArray();
    }

    private static async Task SeedAsync(WebAppFixture fixture, bool mosaic, int noDataPixels)
    {
        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM honua.raster_data WHERE layer_id = @layerId;
            WITH banded AS (
                SELECT piece, ST_AddBand(ST_AddBand(ST_AddBand(
                    ST_MakeEmptyRaster(64 / @parts, 64, -122.5 + piece * 0.15 / @parts, 37.84,
                        0.15 / 64, -0.14 / 64, 0, 0, 4326),
                    '8BUI'::text, CASE WHEN @noData = 4096 THEN 255 ELSE 200 END, 255),
                    '8BUI'::text, CASE WHEN @noData = 4096 THEN 255 ELSE 120 END, 255),
                    '8BUI'::text, CASE WHEN @noData = 4096 THEN 255 ELSE 60 END, 255) rast
                FROM generate_series(0, @parts - 1) piece
            )
            INSERT INTO honua.raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId, 'constant-bin-edges-' || piece,
                CASE WHEN @noData = 1 AND piece = 0
                    THEN ST_SetValue(ST_SetValue(ST_SetValue(rast, 1, 1, 1, 255), 2, 1, 1, 255), 3, 1, 1, 255)
                    ELSE rast END, now(), now()
            FROM banded
            """;
        command.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
        command.Parameters.AddWithValue("parts", mosaic ? 2 : 1);
        command.Parameters.AddWithValue("noData", noDataPixels);
        (await command.ExecuteNonQueryAsync()).Should().BeGreaterThan(0);
    }

    private sealed record SqlBand(long Count, double Sum, double Center);
}
