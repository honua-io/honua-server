// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// #5437: exportImage serves raw pixel blocks of the stored samples — SOAP <c>esriImageBSQ</c>
/// (URL and MIME data) and REST <c>bsq</c>, <c>bip</c> and <c>lerc</c> — and every block decodes
/// to the fixture raster's own pixel values, NoData included.
/// </summary>
[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerRawPixelExportTests
{
    // A 3-band U8 service over the issue's extent, one stored pixel per output pixel at 64 x 64.
    private const int Size = 64;
    private const int BandCount = 3;
    private const double XMin = -122.5;
    private const double YMin = 37.7;
    private const double XMax = -122.35;
    private const double YMax = 37.84;
    private const double CellX = (XMax - XMin) / Size;
    private const double CellY = (YMax - YMin) / Size;
    private const int NoDataColumn = 4;
    private const int NoDataRow = 8;
    private const string SoapRoute = $"/services/{WebAppFixture.TestServiceId}/ImageServer";
    private static readonly string RestExportRoute = $"/rest/services/{WebAppFixture.TestLayerId}/ImageServer/exportImage";

    [IntegrationTheory]
    [InlineData("esriImageReturnURL")]
    [InlineData("esriImageReturnMimeData")]
    [Operation(Operations.Export)]
    [InterfaceOperation(TestProtocols.ImageServer, "ExportImage")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task SoapExportImage_EsriImageBsqFullExtent_ReturnsFixturePixelBlock(string returnType)
    {
        await RunWithFixtureRasterAsync(async fixture =>
        {
            using (var info = await PostSoapAsync(fixture, "<GetServiceInfo xmlns=\"http://www.esri.com/schemas/ArcGIS/10.8\" />"))
            {
                info.Be200Ok();
                XDocument.Parse(await info.Content.ReadAsStringAsync()).Descendants()
                    .Single(element => element.Name.LocalName == "SupportBSQ").Value.Should().Be("true");
            }

            // The issue's exact exchange: full extent, 64 x 64, U8, bilinear, no compression,
            // esriMosaicNone.
            using var response = await PostSoapAsync(fixture, BuildSoapExportImage(
                XMin, YMin, XMax, YMax, Size, Size, "RSP_BilinearInterpolation", returnType));
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            var result = XDocument.Parse(body).Descendants().Single(element => element.Name.LocalName == "Result");
            Child(result, "ImageType").Should().Be("esriImageBSQ");
            Child(result, "ImageWidth").Should().Be("64");
            Child(result, "ImageHeight").Should().Be("64");

            var block = await ReadSoapImageAsync(fixture, result, returnType);
            AssertBsqBlockMatchesFixture(block, 0, 0, Size, Size);
        });
    }

    [IntegrationTheory]
    [InlineData("esriNoDataMatchAny")]
    [InlineData("esriNoDataMatchAll")]
    [Operation(Operations.Export)]
    [InterfaceOperation(TestProtocols.ImageServer, "ExportImage")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task HonuaServer5559_SoapExportImage_ServiceNoDataOverride_ReturnsFixturePixelBlock(
        string noDataInterpretation)
    {
        await RunWithFixtureRasterAsync(async fixture =>
        {
            using var response = await PostSoapAsync(fixture, BuildSoapExportImage(
                XMin,
                YMin,
                XMax,
                YMax,
                Size,
                Size,
                "RSP_NearestNeighbor",
                "esriImageReturnURL",
                noDataInterpretation));
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            var result = XDocument.Parse(body).Descendants().Single(element => element.Name.LocalName == "Result");

            var block = await ReadSoapImageAsync(fixture, result, "esriImageReturnURL");
            AssertBsqBlockMatchesFixture(block, 0, 0, Size, Size);
            var maskOffset = Size * Size * BandCount;
            var noDataPixelIndex = (NoDataRow * Size) + NoDataColumn;
            (block[maskOffset + (noDataPixelIndex / 8)] & (0x80 >> (noDataPixelIndex % 8)))
                .Should().Be(0, "the stored NoData pixel remains masked");
        });
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [InterfaceOperation(TestProtocols.ImageServer, "ExportImage")]
    [Endpoint("POST /services/{serviceId}/ImageServer")]
    public async Task SoapExportImage_EsriImageBsqTwoByTwoNearestNeighbor_ReturnsPointBlock()
    {
        await RunWithFixtureRasterAsync(async fixture =>
        {
            // A point read asks for the 2 x 2 block around the point; this one covers the stored
            // NoData pixel and its three valid neighbours.
            const int column = NoDataColumn;
            const int row = NoDataRow;
            using var response = await PostSoapAsync(fixture, BuildSoapExportImage(
                XMin + (column * CellX),
                YMax - ((row + 2) * CellY),
                XMin + ((column + 2) * CellX),
                YMax - (row * CellY),
                2,
                2,
                "RSP_NearestNeighbor",
                "esriImageReturnURL"));
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            var result = XDocument.Parse(body).Descendants().Single(element => element.Name.LocalName == "Result");

            var block = await ReadSoapImageAsync(fixture, result, "esriImageReturnURL");
            AssertBsqBlockMatchesFixture(block, column, row, 2, 2);
            (block[^1] & 0x80).Should().Be(0, "the first pixel is the fixture's NoData pixel");
        });
    }

    [IntegrationTheory]
    [InlineData("bsq", "image")]
    [InlineData("bsq", "json")]
    [InlineData("bip", "image")]
    [InlineData("bip", "json")]
    [InlineData("lerc", "image")]
    [InlineData("lerc", "json")]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task RestExportImage_RawFormatWithNativePixelType_ReturnsFixturePixelBlock(string format, string f)
    {
        await RunWithFixtureRasterAsync(async fixture =>
        {
            // The issue's REST exchange, plus pixelType=U8: the service's own pixel type is a
            // no-op, not a conversion.
            var query = FormattableString.Invariant(
                $"?bbox={XMin},{YMin},{XMax},{YMax}&bboxSR=4326&size={Size},{Size}&format={format}&pixelType=U8&interpolation=RSP_NearestNeighbor&f={f}");
            using var response = await fixture.Client.GetAsync(RestExportRoute + query);
            byte[] block;
            if (f == "image")
            {
                block = await response.Content.ReadAsByteArrayAsync();
                response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(block));
                response.Content.Headers.ContentType?.MediaType.Should().Be("application/octet-stream");
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync();
                response.StatusCode.Should().Be(HttpStatusCode.OK, body);
                using var json = JsonDocument.Parse(body);
                json.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
                json.RootElement.GetProperty("width").GetInt32().Should().Be(Size);
                json.RootElement.GetProperty("height").GetInt32().Should().Be(Size);
                using var image = await fixture.Client.GetAsync(json.RootElement.GetProperty("href").GetString());
                image.Be200Ok();
                block = await image.Content.ReadAsByteArrayAsync();
            }

            switch (format)
            {
                case "bsq":
                    AssertBsqBlockMatchesFixture(block, 0, 0, Size, Size);
                    break;
                case "bip":
                    AssertBipBlockMatchesFixture(block);
                    break;
                default:
                    AssertLercBlockMatchesFixture(block);
                    break;
            }
        });
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task RestExportImage_PixelTypeOtherThanNative_IsStillNotImplemented()
    {
        await RunWithFixtureRasterAsync(async fixture =>
        {
            var query = FormattableString.Invariant(
                $"?bbox={XMin},{YMin},{XMax},{YMax}&bboxSR=4326&size={Size},{Size}&format=bsq&pixelType=F32&f=json");
            using var response = await fixture.Client.GetAsync(RestExportRoute + query);

            // GeoServices REST reports errors in the JSON error envelope.
            var body = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            var error = json.RootElement.GetProperty("error");
            error.GetProperty("code").GetInt32().Should().Be(501, body);
            error.GetProperty("details")[0].GetString().Should().StartWith("pixelType conversion is not implemented");
        });
    }

    [UnitTest]
    public void LercEncoder_MixedMaskAndConstantBands_DecodesToStoredSamples()
    {
        // Band 1 varies with a NoData pixel; band 2 is constant (LERC2 stores it as zMin only);
        // band 3 is entirely NoData.
        var raster = new RasterResult
        {
            Data = [10, 20, 30, 40, 50, 7, 7, 7, 7, 7, 1, 2, 3, 4, 5],
            ContentType = "application/octet-stream",
            Width = 5,
            Height = 1,
            BandCount = 3,
            PixelType = "8BUI",
            BandValidityMasks = [[0xb8], [0xf8], [0x00]],
        };

        var bands = DecodeLerc(ImageServerPixelBlockEncoder.Encode(raster, ImageServerPixelBlockLayout.Lerc).Data, 5, 1);

        bands.Should().HaveCount(3);
        bands[0].Values.Should().Equal(10, 0, 30, 40, 50);
        bands[0].Valid.Should().Equal(true, false, true, true, true);
        bands[1].Values.Should().Equal(7, 7, 7, 7, 7);
        bands[1].Valid.Should().OnlyContain(valid => valid);
        bands[2].Valid.Should().OnlyContain(valid => !valid);
    }

    [UnitTest]
    public void LercEncoder_FloatSamples_RoundTripBitExact()
    {
        var samples = new[] { -17.25f, 0f, 33.5f, 1e-7f, float.MaxValue, -3.75f };
        var raster = new RasterResult
        {
            Data = samples.SelectMany(BitConverter.GetBytes).ToArray(),
            ContentType = "application/octet-stream",
            Width = 3,
            Height = 2,
            BandCount = 1,
            PixelType = "32BF",
        };

        var blob = ImageServerPixelBlockEncoder.Encode(raster, ImageServerPixelBlockLayout.Lerc).Data;

        BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(34)).Should().Be(6, "dataType 6 is LERC2 float");
        var decoded = DecodeLerc(blob, 3, 2, bytesPerSample: 4).Single();
        decoded.RawSamples.Should().Equal(raster.Data);
    }

    [UnitTest]
    public void BipEncoder_InterleavesBandsPerPixelAndKeepsAnyBandMask()
    {
        var raster = new RasterResult
        {
            Data = [1, 2, 3, 11, 12, 13],
            ContentType = "application/octet-stream",
            Width = 3,
            Height = 1,
            BandCount = 2,
            PixelType = "8BUI",
            BandValidityMasks = [[0x80], [0x20]],
        };

        ImageServerPixelBlockEncoder.Encode(raster, ImageServerPixelBlockLayout.Bip).Data
            .Should().Equal(1, 11, 2, 12, 3, 13, 0xa0);
    }

    private static byte Truth(int band, int column, int row)
    {
        if (column == NoDataColumn && row == NoDataRow)
        {
            return 0;
        }

        // Mirrors the seed expressions below with 1-based [rast.x] = column + 1, [rast.y] = row + 1.
        var x = column + 1;
        var y = row + 1;
        return band switch
        {
            0 => (byte)(1 + (((x * 3) + (y * 5)) % 250)),
            1 => (byte)(1 + (((x * 7) + y) % 250)),
            _ => (byte)(1 + ((x + (y * 11)) % 250)),
        };
    }

    private static bool TruthValid(int column, int row) => column != NoDataColumn || row != NoDataRow;

    private static void AssertBsqBlockMatchesFixture(byte[] block, int column0, int row0, int width, int height)
    {
        var pixels = width * height;
        block.Should().HaveCount((pixels * BandCount) + ((pixels + 7) / 8), "BSQ is U8 samples per band plus a packed mask");
        for (var band = 0; band < BandCount; band++)
        {
            for (var row = 0; row < height; row++)
            {
                for (var column = 0; column < width; column++)
                {
                    var pixel = (row * width) + column;
                    var expected = Truth(band, column0 + column, row0 + row);
                    block[(band * pixels) + pixel].Should().Be(
                        expected, $"band {band + 1}, column {column0 + column}, row {row0 + row}");
                }
            }
        }

        AssertMask(block.AsSpan(pixels * BandCount), column0, row0, width, height);
    }

    private static void AssertBipBlockMatchesFixture(byte[] block)
    {
        const int pixels = Size * Size;
        block.Should().HaveCount((pixels * BandCount) + (pixels / 8));
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            for (var band = 0; band < BandCount; band++)
            {
                block[(pixel * BandCount) + band].Should().Be(
                    Truth(band, pixel % Size, pixel / Size), $"band {band + 1}, pixel {pixel}");
            }
        }

        AssertMask(block.AsSpan(pixels * BandCount), 0, 0, Size, Size);
    }

    private static void AssertLercBlockMatchesFixture(byte[] block)
    {
        var bands = DecodeLerc(block, Size, Size);
        bands.Should().HaveCount(BandCount, "LERC carries one blob per band");
        for (var band = 0; band < BandCount; band++)
        {
            for (var pixel = 0; pixel < Size * Size; pixel++)
            {
                var (column, row) = (pixel % Size, pixel / Size);
                bands[band].Valid[pixel].Should().Be(TruthValid(column, row), $"band {band + 1}, pixel {pixel}");
                if (TruthValid(column, row))
                {
                    bands[band].Values[pixel].Should().Be(Truth(band, column, row), $"band {band + 1}, pixel {pixel}");
                }
            }
        }
    }

    private static void AssertMask(ReadOnlySpan<byte> mask, int column0, int row0, int width, int height)
    {
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var pixel = (row * width) + column;
                var valid = (mask[pixel / 8] & (0x80 >> (pixel % 8))) != 0;
                valid.Should().Be(TruthValid(column0 + column, row0 + row), $"mask bit for pixel {pixel}");
            }
        }
    }

    private sealed record LercBand(double[] Values, bool[] Valid, byte[] RawSamples);

    // An independent reader of the LERC2 blob layout (header, Fletcher-32 checksum, run-length
    // mask, one-sweep samples), so the test fails if the wire framing drifts from the format.
    private static List<LercBand> DecodeLerc(byte[] data, int width, int height, int bytesPerSample = 1)
    {
        var bands = new List<LercBand>();
        var offset = 0;
        while (offset < data.Length)
        {
            var blob = data.AsSpan(offset);
            Encoding.ASCII.GetString(blob[..6]).Should().Be("Lerc2 ");
            BinaryPrimitives.ReadInt32LittleEndian(blob[6..]).Should().Be(3);
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(blob[10..]);
            BinaryPrimitives.ReadInt32LittleEndian(blob[14..]).Should().Be(height);
            BinaryPrimitives.ReadInt32LittleEndian(blob[18..]).Should().Be(width);
            var validCount = BinaryPrimitives.ReadInt32LittleEndian(blob[22..]);
            var blobSize = BinaryPrimitives.ReadInt32LittleEndian(blob[30..]);
            var dataType = BinaryPrimitives.ReadInt32LittleEndian(blob[34..]);
            var zMin = BinaryPrimitives.ReadDoubleLittleEndian(blob[46..]);
            var zMax = BinaryPrimitives.ReadDoubleLittleEndian(blob[54..]);
            blob = blob[..blobSize];
            Fletcher32(blob[14..]).Should().Be(checksum, "the LERC2 checksum covers every byte after it");

            var pixels = width * height;
            var valid = new bool[pixels];
            var maskBytes = BinaryPrimitives.ReadInt32LittleEndian(blob[62..]);
            var position = 66;
            if (validCount == pixels)
            {
                Array.Fill(valid, true);
            }
            else if (validCount > 0)
            {
                var mask = new List<byte>();
                var end = position + maskBytes;
                while (true)
                {
                    var count = BinaryPrimitives.ReadInt16LittleEndian(blob[position..]);
                    position += 2;
                    if (count == short.MinValue)
                    {
                        break;
                    }

                    if (count > 0)
                    {
                        mask.AddRange(blob.Slice(position, count).ToArray());
                        position += count;
                    }
                    else
                    {
                        mask.AddRange(Enumerable.Repeat(blob[position], -count));
                        position++;
                    }
                }

                position.Should().Be(end);
                for (var pixel = 0; pixel < pixels; pixel++)
                {
                    valid[pixel] = (mask[pixel / 8] & (0x80 >> (pixel % 8))) != 0;
                }
            }

            valid.Count(static bit => bit).Should().Be(validCount);
            var values = new double[pixels];
            var raw = new byte[pixels * bytesPerSample];
            if (validCount > 0 && zMin == zMax)
            {
                position.Should().Be(blobSize, "a constant band carries no samples");
                for (var pixel = 0; pixel < pixels; pixel++)
                {
                    values[pixel] = valid[pixel] ? zMin : 0;
                }
            }
            else if (validCount > 0)
            {
                blob[position++].Should().Be(1, "samples are stored in one sweep");
                for (var pixel = 0; pixel < pixels; pixel++)
                {
                    if (!valid[pixel])
                    {
                        continue;
                    }

                    var sample = blob.Slice(position, bytesPerSample);
                    sample.CopyTo(raw.AsSpan(pixel * bytesPerSample));
                    values[pixel] = dataType switch
                    {
                        1 => sample[0],
                        6 => BinaryPrimitives.ReadSingleLittleEndian(sample),
                        _ => throw new NotSupportedException($"Test reader does not decode dataType {dataType}."),
                    };
                    position += bytesPerSample;
                }

                position.Should().Be(blobSize);
            }

            bands.Add(new LercBand(values, valid, raw));
            offset += blobSize;
        }

        return bands;
    }

    private static uint Fletcher32(ReadOnlySpan<byte> bytes)
    {
        uint sum1 = 0xffff, sum2 = 0xffff;
        var index = 0;
        for (var words = bytes.Length / 2; words > 0;)
        {
            var block = Math.Min(words, 359);
            words -= block;
            while (block-- > 0)
            {
                sum1 += (uint)((bytes[index] << 8) | bytes[index + 1]);
                index += 2;
                sum2 += sum1;
            }

            sum1 = (sum1 & 0xffff) + (sum1 >> 16);
            sum2 = (sum2 & 0xffff) + (sum2 >> 16);
        }

        if ((bytes.Length & 1) != 0)
        {
            sum1 += (uint)bytes[index] << 8;
            sum2 += sum1;
        }

        sum1 = (sum1 & 0xffff) + (sum1 >> 16);
        sum2 = (sum2 & 0xffff) + (sum2 >> 16);
        return (sum2 << 16) | sum1;
    }

    private static string BuildSoapExportImage(
        double xMin,
        double yMin,
        double xMax,
        double yMax,
        int width,
        int height,
        string interpolation,
        string returnType,
        string? noDataInterpretation = null)
        => FormattableString.Invariant($"""
            <ExportImage xmlns="http://www.esri.com/schemas/ArcGIS/10.8"
                         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                         xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <ImageDescription xsi:type="GeoImageDescription">
                <Compression>None</Compression>
                <Extent xsi:type="EnvelopeN">
                  <XMin>{xMin:R}</XMin><YMin>{yMin:R}</YMin><XMax>{xMax:R}</XMax><YMax>{yMax:R}</YMax>
                  <SpatialReference xsi:type="GeographicCoordinateSystem"><WKID>4326</WKID></SpatialReference>
                </Extent>
                <Height>{height}</Height>
                <Interpolation>{interpolation}</Interpolation>
                <MosaicRule xsi:type="MosaicRule"><MosaicMethod>esriMosaicNone</MosaicMethod></MosaicRule>
                <PixelType>U8</PixelType>
                {(noDataInterpretation is null ? string.Empty : $"<NoData xsi:type=\"xsd:base64Binary\">AAAA</NoData><NoDataInterpretation>{noDataInterpretation}</NoDataInterpretation>")}
                <Width>{width}</Width>
              </ImageDescription>
              <ImageType xsi:type="ImageType">
                <ImageFormat>esriImageBSQ</ImageFormat>
                <ImageReturnType>{returnType}</ImageReturnType>
              </ImageType>
            </ExportImage>
            """);

    private static async Task<HttpResponseMessage> PostSoapAsync(WebAppFixture fixture, string operation)
    {
        var envelope = $"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>{operation}</soap:Body>
            </soap:Envelope>
            """;
        using var content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        return await fixture.Client.PostAsync(SoapRoute, content);
    }

    private static async Task<byte[]> ReadSoapImageAsync(WebAppFixture fixture, XElement result, string returnType)
    {
        if (returnType == "esriImageReturnMimeData")
        {
            return Convert.FromBase64String(Child(result, "ImageData"));
        }

        using var image = await fixture.Client.GetAsync(Child(result, "ImageURL"));
        image.Be200Ok();
        return await image.Content.ReadAsByteArrayAsync();
    }

    private static string Child(XElement element, string localName)
        => element.Elements().Single(child => child.Name.LocalName == localName).Value;

    private static async Task RunWithFixtureRasterAsync(Func<WebAppFixture, Task> action)
    {
        var fixture = new WebAppFixture();
        await fixture.InitializeAsync();
        try
        {
            // honua.raster_data is process-global: hold the schema-mutation lock across seeding
            // and every request so another collection cannot swap the layer's rasters mid-test.
            await fixture.Postgres.RunUnderSchemaMutationLockAsync(async () =>
            {
                await SeedFixtureRasterAsync(fixture);
                await action(fixture);
            });
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task SeedFixtureRasterAsync(WebAppFixture fixture)
    {
        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema!);
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM honua.raster_data WHERE layer_id = @layerId;";
            delete.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
            await delete.ExecuteNonQueryAsync();
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            WITH base AS (
                SELECT ST_AddBand(
                    ST_MakeEmptyRaster(@size, @size, @xmin, @ymax, @cellx, -@celly, 0, 0, 4326),
                    '8BUI'::text, 1, 0) AS rast
            ), banded AS (
                SELECT ST_AddBand(ST_AddBand(
                    ST_MapAlgebra(rast, 1, '8BUI', '1 + (([rast.x] * 3 + [rast.y] * 5) % 250)', 0),
                    ST_MapAlgebra(rast, 1, '8BUI', '1 + (([rast.x] * 7 + [rast.y]) % 250)', 0)),
                    ST_MapAlgebra(rast, 1, '8BUI', '1 + (([rast.x] + [rast.y] * 11) % 250)', 0)) AS rast
                FROM base
            )
            INSERT INTO honua.raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   'issue-5437-u8',
                   ST_SetValue(ST_SetValue(ST_SetValue(rast, 1, @nodataX, @nodataY, 0), 2, @nodataX, @nodataY, 0), 3, @nodataX, @nodataY, 0),
                   now(),
                   now()
            FROM banded;
            """;
        insert.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
        insert.Parameters.AddWithValue("size", Size);
        insert.Parameters.AddWithValue("xmin", XMin);
        insert.Parameters.AddWithValue("ymax", YMax);
        insert.Parameters.AddWithValue("cellx", CellX);
        insert.Parameters.AddWithValue("celly", CellY);
        insert.Parameters.AddWithValue("nodataX", NoDataColumn + 1);
        insert.Parameters.AddWithValue("nodataY", NoDataRow + 1);
        (await insert.ExecuteNonQueryAsync()).Should().Be(1);
    }
}
