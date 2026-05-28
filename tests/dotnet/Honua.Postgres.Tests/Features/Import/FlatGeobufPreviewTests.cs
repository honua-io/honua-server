// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FlatGeobuf;
using FlatGeobuf.NTS;
using Google.FlatBuffers;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;
using Honua.TestKit.Infrastructure;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class FlatGeobufPreviewTests
{
    [Fact]
    public async Task PreviewFileAsync_FlatGeobuf_ReturnsPreviewMetadata()
    {
        await using var stream = CreateFlatGeobufStream();
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "sample.fgb");

        preview.Format.Should().Be(SupportedFileFormat.FlatGeobuf);
        // The FlatGeobuf NTS Serialize API does not encode CRS into the header,
        // so SRID detection correctly returns null for library-serialized fixtures.
        preview.DetectedSrid.Should().BeNull();
        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties.Should().ContainKey("name");
        preview.SampleProperties["name"].Should().Be("Test Feature");
    }

    [Fact]
    public void DetectFormat_AndSupportedExtensions_IncludeFlatGeobuf()
    {
        var service = CreateService();

        service.DetectFormat("sample.fgb").Should().Be(SupportedFileFormat.FlatGeobuf);
        service.GetSupportedExtensions().Should().Contain(".fgb");
    }

    [Fact]
    public async Task PreviewFileAsync_FlatGeobuf_NonSeekableStream_DetectsCrsAndYieldsFeatures()
    {
        await using var innerStream = CreateFlatGeobufStream();
        await using var stream = new NonSeekableStream(innerStream);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "sample.fgb");

        preview.Format.Should().Be(SupportedFileFormat.FlatGeobuf);
        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties.Should().ContainKey("name");
        preview.SampleProperties["name"].Should().Be("Test Feature");
        // Same as seekable variant: NTS Serialize does not write CRS into header.
        preview.DetectedSrid.Should().BeNull();
    }

    [Fact]
    public async Task PreviewFileAsync_FlatGeobuf_WithEmbeddedCrs_DetectsEpsg4326()
    {
        await using var stream = CreateFlatGeobufStreamWithCrs(4326);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "with_crs.fgb");

        preview.Format.Should().Be(SupportedFileFormat.FlatGeobuf);
        preview.DetectedSrid.Should().Be(4326);
        preview.TotalFeatureCount.Should().Be(1);
    }

    [Fact]
    public async Task PreviewFileAsync_FlatGeobuf_LargeHeaderNonSeekable_DetectsCrs()
    {
        // Regression: FlatGeobuf headers with many columns can exceed the 8 KB
        // CRS-detection buffer. Verify that the non-seekable path spills to a
        // seekable temp file and still detects the CRS from the full header.
        await using var innerStream = CreateFlatGeobufStreamWithLargeHeader(4326, columnCount: 200);
        await using var stream = new NonSeekableStream(innerStream);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "large_header.fgb");

        preview.Format.Should().Be(SupportedFileFormat.FlatGeobuf);
        preview.DetectedSrid.Should().Be(4326);
        preview.TotalFeatureCount.Should().Be(0);
    }

    [Fact]
    public async Task PreviewFileAsync_FlatGeobuf_OgcCrs84Authority_MapsTo4326()
    {
        // FlatGeobuf files written with Org="OGC", CodeString="CRS84", Code=0
        // must resolve to SRID 4326 (WGS 84 lon/lat).
        await using var stream = CreateFlatGeobufStreamWithAuthorityCrs("OGC", "CRS84");
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "ogc_crs84.fgb");

        preview.Format.Should().Be(SupportedFileFormat.FlatGeobuf);
        preview.DetectedSrid.Should().Be(4326);
    }

    [Fact]
    public async Task PreviewFileAsync_FlatGeobuf_EpsgCodeStringAuthority_DetectsSrid()
    {
        // FlatGeobuf files written with Org="EPSG", CodeString="32632", Code=0
        // must resolve to SRID 32632.
        await using var stream = CreateFlatGeobufStreamWithAuthorityCrs("EPSG", "32632");
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "epsg_codestring.fgb");

        preview.Format.Should().Be(SupportedFileFormat.FlatGeobuf);
        preview.DetectedSrid.Should().Be(32632);
    }

    [Fact]
    public async Task PreviewFileAsync_FlatGeobuf_TotalFeatureCountUsesHeaderNotSampleCap()
    {
        // Create a FlatGeobuf file whose header declares FeaturesCount = 5 and
        // contains 5 actual feature bytes. With MaxPreviewFeatures = 2, the
        // preview must report TotalFeatureCount = 5 (from header), not 2 (sample cap).
        // NTS Serialize writes FeaturesCount = 0 (unknown), so we splice a custom
        // header with the correct count in front of NTS-serialized feature data.
        await using var stream = CreateFlatGeobufStreamWithExplicitCount(5);
        var service = PreviewImportServiceFactory.Create(
            new ImportLimits { MaxPreviewFeatures = 2 });

        var preview = await service.PreviewFileAsync(stream, "multi.fgb");

        preview.Format.Should().Be(SupportedFileFormat.FlatGeobuf);
        preview.TotalFeatureCount.Should().Be(5,
            "TotalFeatureCount should reflect the header's feature count, not the capped sample size");
        preview.SampleProperties.Should().ContainKey("name",
            "sample properties should still be populated from the first feature");
    }

    [Fact]
    public void ResolveSrid_NumericCode_ReturnsCode()
    {
        var header = BuildHeaderWithCrs(code: 4326);
        FlatGeobufFormatReader.ResolveSrid(header).Should().Be(4326);
    }

    [Fact]
    public void ResolveSrid_OgcCrs84_Returns4326()
    {
        var header = BuildHeaderWithCrs(org: "OGC", codeString: "CRS84");
        FlatGeobufFormatReader.ResolveSrid(header).Should().Be(4326);
    }

    [Fact]
    public void ResolveSrid_EpsgCodeString_ReturnsCode()
    {
        var header = BuildHeaderWithCrs(org: "EPSG", codeString: "32632");
        FlatGeobufFormatReader.ResolveSrid(header).Should().Be(32632);
    }

    [Fact]
    public void ResolveSrid_NoCrs_ReturnsNull()
    {
        var header = BuildHeaderWithCrs();
        FlatGeobufFormatReader.ResolveSrid(header).Should().BeNull();
    }

    [Fact]
    public void ResolveSrid_UnknownAuthority_ReturnsNull()
    {
        var header = BuildHeaderWithCrs(org: "CUSTOM", codeString: "MYCRS");
        FlatGeobufFormatReader.ResolveSrid(header).Should().BeNull();
    }

    [Fact]
    public void ResolveSrid_NonEpsgNumericAuthority_ReturnsNull()
    {
        // ESRI:102100 is Esri's Web Mercator — not an EPSG code.
        // The numeric code must not be blindly returned as an SRID.
        var header = BuildHeaderWithCrs(code: 102100, org: "ESRI");
        FlatGeobufFormatReader.ResolveSrid(header).Should().BeNull();
    }

    [Fact]
    public void ResolveSrid_ExplicitEpsgOrg_WithNumericCode_ReturnsCode()
    {
        var header = BuildHeaderWithCrs(code: 32632, org: "EPSG");
        FlatGeobufFormatReader.ResolveSrid(header).Should().Be(32632);
    }

    [Fact]
    public void ExtractCrsWkt_WktOnlyHeader_ReturnsWktString()
    {
        var wkt = "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\"],AUTHORITY[\"EPSG\",\"4326\"]]";
        var header = BuildHeaderWithWkt(wkt);
        FlatGeobufFormatReader.ExtractCrsWkt(header).Should().Be(wkt);
    }

    [Fact]
    public void ExtractCrsWkt_NoCrs_ReturnsNull()
    {
        var header = BuildHeaderWithCrs();
        FlatGeobufFormatReader.ExtractCrsWkt(header).Should().BeNull();
    }

    [Fact]
    public void ReadCrsInfo_NumericCode_ReturnsSridWithoutWkt()
    {
        using var stream = CreateFlatGeobufStreamWithCrs(4326);
        var (srid, crsWkt) = FlatGeobufFormatReader.ReadCrsInfo(stream);
        srid.Should().Be(4326);
        crsWkt.Should().BeNull();
    }

    [Fact]
    public void ReadCrsInfo_WktOnly_ReturnsNullSridWithWkt()
    {
        var wkt = "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\"],AUTHORITY[\"EPSG\",\"4326\"]]";
        using var stream = CreateFlatGeobufStreamWithWktCrs(wkt);
        var (srid, crsWkt) = FlatGeobufFormatReader.ReadCrsInfo(stream);
        srid.Should().BeNull();
        crsWkt.Should().Be(wkt);
    }

    [Fact]
    public async Task PreviewFileAsync_FlatGeobuf_UnknownHeaderCount_ReportsFullStreamedCount()
    {
        // NTS Serialize writes FeaturesCount=0 (unknown). With MaxPreviewFeatures=2,
        // the preview must still report TotalFeatureCount=5 by streaming to EOF.
        await using var stream = CreateFlatGeobufStreamWithNFeatures(5);
        var service = PreviewImportServiceFactory.Create(
            new ImportLimits { MaxPreviewFeatures = 2 });

        var preview = await service.PreviewFileAsync(stream, "unknown_count.fgb");

        preview.Format.Should().Be(SupportedFileFormat.FlatGeobuf);
        preview.TotalFeatureCount.Should().Be(5,
            "TotalFeatureCount should reflect the true streamed count, not the capped sample size");
        preview.SampleProperties.Should().ContainKey("name",
            "sample properties should still be populated from the first feature");
    }

    [Fact]
    public async Task PreviewFileAsync_FlatGeobuf_UnknownHeaderCount_CapsAtMaxPreviewCountScan()
    {
        // When FeaturesCount=0 and the file exceeds MaxPreviewCountScan, the
        // preview must stop scanning and report the capped count rather than
        // streaming the entire file.
        await using var stream = CreateFlatGeobufStreamWithNFeatures(10);
        var service = PreviewImportServiceFactory.Create(
            new ImportLimits { MaxPreviewFeatures = 2, MaxPreviewCountScan = 6 });

        var preview = await service.PreviewFileAsync(stream, "capped_count.fgb");

        preview.Format.Should().Be(SupportedFileFormat.FlatGeobuf);
        preview.TotalFeatureCount.Should().Be(6,
            "TotalFeatureCount should be capped at MaxPreviewCountScan, not the full file count");
        preview.SampleProperties.Should().ContainKey("name");
    }

    private static MemoryStream CreateFlatGeobufStream()
        => CreateFlatGeobufStreamWithNFeatures(1);

    private static MemoryStream CreateFlatGeobufStreamWithNFeatures(int count)
    {
        var columns = new List<ColumnMeta>
        {
            new() { Name = "name", Type = ColumnType.String }
        };

        var features = new List<NetTopologySuite.Features.Feature>(count);
        for (var i = 0; i < count; i++)
        {
            var attributes = new AttributesTable();
            attributes.Add("name", i == 0 ? "Test Feature" : $"Feature {i}");
            var point = new Point(-122.4194 + i * 0.001, 37.7749) { SRID = 4326 };
            features.Add(new NetTopologySuite.Features.Feature(point, attributes));
        }

        var stream = new MemoryStream();
        FeatureCollectionConversions.Serialize(
            stream,
            features,
            FlatGeobuf.GeometryType.Point,
            2,
            columns);
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Builds a FlatGeobuf file with <paramref name="featureCount"/> features and a
    /// header that declares the correct <c>FeaturesCount</c>. NTS Serialize writes
    /// <c>FeaturesCount = 0</c> (unknown), so we splice a custom header with the
    /// correct count in front of NTS-serialized feature data.
    /// </summary>
    private static MemoryStream CreateFlatGeobufStreamWithExplicitCount(int featureCount)
    {
        var columns = new List<ColumnMeta>
        {
            new() { Name = "name", Type = ColumnType.String }
        };

        // Build features
        var features = new List<NetTopologySuite.Features.Feature>(featureCount);
        for (var i = 0; i < featureCount; i++)
        {
            var attrs = new AttributesTable();
            attrs.Add("name", $"Feature {i}");
            features.Add(new NetTopologySuite.Features.Feature(
                new Point(i * 0.001, 0) { SRID = 4326 }, attrs));
        }

        // Serialize via NTS (produces valid feature bytes but FeaturesCount = 0)
        using var ntsStream = new MemoryStream();
        FeatureCollectionConversions.Serialize(ntsStream, features,
            FlatGeobuf.GeometryType.Point, 2, columns);
        ntsStream.Position = 0;

        // Read past NTS header: magic (8) + size-prefixed FlatBuffer header
        var magic = new byte[8];
        ntsStream.ReadExactly(magic);
        var sizePrefix = new byte[4];
        ntsStream.ReadExactly(sizePrefix);
        var ntsHeaderSize = BitConverter.ToInt32(sizePrefix);

        // Parse the NTS header to replicate its column definitions
        ntsStream.Position = 0;
        var ntsHeader = Helpers.ReadHeader(ntsStream);

        // Build custom header with the correct FeaturesCount
        var builder = new FlatBufferBuilder(1024);
        var colOffsets = new Offset<FlatGeobuf.Column>[ntsHeader.ColumnsLength];
        for (var i = 0; i < ntsHeader.ColumnsLength; i++)
        {
            var col = ntsHeader.Columns(i)!.Value;
            var nameOff = builder.CreateString(col.Name);
            colOffsets[i] = FlatGeobuf.Column.CreateColumn(builder, nameOff, col.Type);
        }

        var colsVector = FlatGeobuf.Header.CreateColumnsVector(builder, colOffsets);
        FlatGeobuf.Header.StartHeader(builder);
        FlatGeobuf.Header.AddGeometryType(builder, ntsHeader.GeometryType);
        FlatGeobuf.Header.AddFeaturesCount(builder, (ulong)featureCount);
        FlatGeobuf.Header.AddColumns(builder, colsVector);
        FlatGeobuf.Header.AddIndexNodeSize(builder, 0);
        var headerOffset = FlatGeobuf.Header.EndHeader(builder);
        builder.FinishSizePrefixed(headerOffset.Value);
        var headerBytes = builder.SizedByteArray();

        // Skip past NTS header to reach feature data
        ntsStream.Position = 8 + 4 + ntsHeaderSize;

        // Assemble: magic + custom header + NTS feature data
        var result = new MemoryStream();
        result.Write(Constants.MagicBytes);
        result.Write(headerBytes);
        ntsStream.CopyTo(result);
        result.Position = 0;
        return result;
    }

    /// <summary>
    /// Builds a minimal valid FlatGeobuf file with a CRS code in the header.
    /// The NTS Serialize API does not write CRS, so we construct the binary
    /// directly using the FlatBuffer builder to exercise SRID detection.
    /// </summary>
    private static MemoryStream CreateFlatGeobufStreamWithCrs(int epsgCode)
    {
        var columns = new List<ColumnMeta>
        {
            new() { Name = "name", Type = ColumnType.String }
        };

        // Serialize one feature using the NTS API into a separate stream to get
        // valid feature bytes (skip its header — we'll use our custom one).
        var attributes = new AttributesTable();
        attributes.Add("name", "CRS Test");
        var point = new Point(0, 0) { SRID = epsgCode };
        var feature = new NetTopologySuite.Features.Feature(point, attributes);

        // Build a complete file: use the NTS serializer (which produces a valid
        // file without CRS), then splice our CRS-bearing header in front of the
        // feature data.
        using var ntsStream = new MemoryStream();
        FeatureCollectionConversions.Serialize(ntsStream, new[] { feature },
            FlatGeobuf.GeometryType.Point, 2, columns);
        ntsStream.Position = 0;

        // Read past NTS header: magic (8) + size-prefixed FlatBuffer header
        var magic = new byte[8];
        ntsStream.ReadExactly(magic);
        var sizePrefix = new byte[4];
        ntsStream.ReadExactly(sizePrefix);
        var ntsHeaderSize = BitConverter.ToInt32(sizePrefix);

        // Parse the NTS header to replicate its column definitions in our custom header
        ntsStream.Position = 0;
        var ntsHeader = Helpers.ReadHeader(ntsStream);

        // Build custom header with CRS and matching column definitions
        var builder = new FlatBufferBuilder(512);
        var colOffsets = new Offset<FlatGeobuf.Column>[ntsHeader.ColumnsLength];
        for (var i = 0; i < ntsHeader.ColumnsLength; i++)
        {
            var col = ntsHeader.Columns(i)!.Value;
            var nameOff = builder.CreateString(col.Name);
            colOffsets[i] = FlatGeobuf.Column.CreateColumn(builder, nameOff, col.Type);
        }

        var colsVector = FlatGeobuf.Header.CreateColumnsVector(builder, colOffsets);
        var crsOffset = FlatGeobuf.Crs.CreateCrs(builder, code: epsgCode);
        FlatGeobuf.Header.StartHeader(builder);
        FlatGeobuf.Header.AddGeometryType(builder, FlatGeobuf.GeometryType.Point);
        FlatGeobuf.Header.AddCrs(builder, crsOffset);
        FlatGeobuf.Header.AddFeaturesCount(builder, 1);
        FlatGeobuf.Header.AddColumns(builder, colsVector);
        FlatGeobuf.Header.AddIndexNodeSize(builder, 0);
        var headerOffset = FlatGeobuf.Header.EndHeader(builder);
        builder.FinishSizePrefixed(headerOffset.Value);
        var headerBytes = builder.SizedByteArray();

        // Skip past NTS header to reach feature data
        ntsStream.Position = 8 + 4 + ntsHeaderSize;

        // Assemble: magic + our header + NTS feature data
        var result = new MemoryStream();
        result.Write(Constants.MagicBytes);
        result.Write(headerBytes);
        ntsStream.CopyTo(result);
        result.Position = 0;
        return result;
    }

    /// <summary>
    /// Builds a valid FlatGeobuf file with an embedded CRS and <paramref name="columnCount"/>
    /// columns so the FlatBuffer header exceeds the 8 KB CRS-detection buffer.
    /// Features count is zero — the test exercises CRS detection, not feature parsing.
    /// </summary>
    private static MemoryStream CreateFlatGeobufStreamWithLargeHeader(int epsgCode, int columnCount)
    {
        var builder = new FlatBufferBuilder(16384);

        // Build column offsets first (FlatBuffers requires bottom-up construction)
        var columnOffsets = new Offset<FlatGeobuf.Column>[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            var nameOffset = builder.CreateString($"column_description_field_{i:D4}");
            columnOffsets[i] = FlatGeobuf.Column.CreateColumn(builder, nameOffset, FlatGeobuf.ColumnType.String);
        }

        var columnsVector = FlatGeobuf.Header.CreateColumnsVector(builder, columnOffsets);
        var crsOffset = FlatGeobuf.Crs.CreateCrs(builder, code: epsgCode);

        FlatGeobuf.Header.StartHeader(builder);
        FlatGeobuf.Header.AddGeometryType(builder, FlatGeobuf.GeometryType.Point);
        FlatGeobuf.Header.AddCrs(builder, crsOffset);
        FlatGeobuf.Header.AddFeaturesCount(builder, 0);
        FlatGeobuf.Header.AddIndexNodeSize(builder, 0);
        FlatGeobuf.Header.AddColumns(builder, columnsVector);
        var headerOffset = FlatGeobuf.Header.EndHeader(builder);
        builder.FinishSizePrefixed(headerOffset.Value);
        var headerBytes = builder.SizedByteArray();

        // Assemble: magic + header (no features)
        var result = new MemoryStream();
        result.Write(Constants.MagicBytes);
        result.Write(headerBytes);
        result.Position = 0;
        return result;
    }

    /// <summary>
    /// Builds a minimal FlatGeobuf file with a CRS using authority Org/CodeString (Code=0).
    /// Exercises the codeString-based CRS resolution path in <see cref="FlatGeobufFormatReader.ResolveSrid"/>.
    /// </summary>
    private static MemoryStream CreateFlatGeobufStreamWithAuthorityCrs(string org, string codeString)
    {
        var builder = new FlatBufferBuilder(512);
        var orgOffset = builder.CreateString(org);
        var codeStringOffset = builder.CreateString(codeString);
        var crsOffset = FlatGeobuf.Crs.CreateCrs(builder, orgOffset, 0, default, default, default, codeStringOffset);
        FlatGeobuf.Header.StartHeader(builder);
        FlatGeobuf.Header.AddGeometryType(builder, FlatGeobuf.GeometryType.Point);
        FlatGeobuf.Header.AddCrs(builder, crsOffset);
        FlatGeobuf.Header.AddFeaturesCount(builder, 0);
        FlatGeobuf.Header.AddIndexNodeSize(builder, 0);
        var headerOffset = FlatGeobuf.Header.EndHeader(builder);
        builder.FinishSizePrefixed(headerOffset.Value);
        var headerBytes = builder.SizedByteArray();

        var result = new MemoryStream();
        result.Write(Constants.MagicBytes);
        result.Write(headerBytes);
        result.Position = 0;
        return result;
    }

    /// <summary>
    /// Builds a parsed FlatGeobuf <see cref="Header"/> with optional CRS fields for unit-testing
    /// <see cref="FlatGeobufFormatReader.ResolveSrid"/>. Uses <see cref="Helpers.ReadHeader(Stream)"/>
    /// to produce the same Header struct the production code receives.
    /// </summary>
    private static Header BuildHeaderWithCrs(int code = 0, string? org = null, string? codeString = null)
    {
        var builder = new FlatBufferBuilder(256);

        Offset<FlatGeobuf.Crs>? crsOffset = null;
        if (code > 0 || org != null || codeString != null)
        {
            var orgOff = org != null ? builder.CreateString(org) : default;
            var csOff = codeString != null ? builder.CreateString(codeString) : default;
            crsOffset = FlatGeobuf.Crs.CreateCrs(builder, orgOff, code, default, default, default, csOff);
        }

        FlatGeobuf.Header.StartHeader(builder);
        FlatGeobuf.Header.AddGeometryType(builder, FlatGeobuf.GeometryType.Point);
        if (crsOffset.HasValue)
            FlatGeobuf.Header.AddCrs(builder, crsOffset.Value);
        FlatGeobuf.Header.AddFeaturesCount(builder, 0);
        var headerOffset = FlatGeobuf.Header.EndHeader(builder);
        builder.FinishSizePrefixed(headerOffset.Value);
        var headerBytes = builder.SizedByteArray();

        // Feed through Helpers.ReadHeader (which expects magic + size-prefixed header)
        // to produce the same Header struct the production code uses.
        using var ms = new MemoryStream();
        ms.Write(Constants.MagicBytes);
        ms.Write(headerBytes);
        ms.Position = 0;
        return Helpers.ReadHeader(ms);
    }

    /// <summary>
    /// Builds a minimal FlatGeobuf file with CRS encoded as WKT only (no org/code/codeString).
    /// Exercises the WKT fallback path in CRS detection.
    /// </summary>
    private static MemoryStream CreateFlatGeobufStreamWithWktCrs(string wkt)
    {
        var builder = new FlatBufferBuilder(4096);
        var wktOffset = builder.CreateString(wkt);
        var crsOffset = FlatGeobuf.Crs.CreateCrs(builder, default, 0, default, default, wktOffset, default);
        FlatGeobuf.Header.StartHeader(builder);
        FlatGeobuf.Header.AddGeometryType(builder, FlatGeobuf.GeometryType.Point);
        FlatGeobuf.Header.AddCrs(builder, crsOffset);
        FlatGeobuf.Header.AddFeaturesCount(builder, 0);
        FlatGeobuf.Header.AddIndexNodeSize(builder, 0);
        var headerOffset = FlatGeobuf.Header.EndHeader(builder);
        builder.FinishSizePrefixed(headerOffset.Value);
        var headerBytes = builder.SizedByteArray();

        var result = new MemoryStream();
        result.Write(Constants.MagicBytes);
        result.Write(headerBytes);
        result.Position = 0;
        return result;
    }

    /// <summary>
    /// Builds a parsed FlatGeobuf <see cref="Header"/> with a WKT-only CRS field.
    /// </summary>
    private static Header BuildHeaderWithWkt(string wkt)
    {
        var builder = new FlatBufferBuilder(4096);
        var wktOff = builder.CreateString(wkt);
        var crsOffset = FlatGeobuf.Crs.CreateCrs(builder, default, 0, default, default, wktOff, default);

        FlatGeobuf.Header.StartHeader(builder);
        FlatGeobuf.Header.AddGeometryType(builder, FlatGeobuf.GeometryType.Point);
        FlatGeobuf.Header.AddCrs(builder, crsOffset);
        FlatGeobuf.Header.AddFeaturesCount(builder, 0);
        var headerOffset = FlatGeobuf.Header.EndHeader(builder);
        builder.FinishSizePrefixed(headerOffset.Value);
        var headerBytes = builder.SizedByteArray();

        using var ms = new MemoryStream();
        ms.Write(Constants.MagicBytes);
        ms.Write(headerBytes);
        ms.Position = 0;
        return Helpers.ReadHeader(ms);
    }

    private static IFileImportService CreateService() =>
        PreviewImportServiceFactory.Create();

}
