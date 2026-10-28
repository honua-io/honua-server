// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.FileImport.Services;
using NetTopologySuite.IO;
using Parquet;
using Parquet.Schema;
using ParquetDataColumn = Parquet.Data.DataColumn;

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;

namespace Honua.Db.Postgres.Tests.Features.Import;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GeoParquetActiveCoverageTests
{
    [UnitTest]
    public async Task PreviewFileAsync_GeoParquetNonseekableInput_ReadsBufferedCopyAndReleasesScratch()
    {
        await using var payload = await GeoParquetTestFactory.CreateStreamAsync();
        await using var input = new CapturedNonseekableStream(payload.ToArray());
        var preview = await PreviewImportServiceFactory.Create().PreviewFileAsync(input, "nonseekable.parquet");
        preview.TotalFeatureCount.Should().Be(1);
        input.ScratchDirectory.Should().NotBeNull();
        Directory.Exists(input.ScratchDirectory).Should().BeFalse();
        input.CanRead.Should().BeTrue("the input stream belongs to the caller");
    }

    [UnitTest]
    public async Task ImportFileAsync_GeoParquetCanceledAfterScratchCopy_ReleasesUntransferredScratch()
    {
        using var cancellation = new CancellationTokenSource();
        await using var payload = await GeoParquetTestFactory.CreateStreamAsync();
        await using var input = new CapturedNonseekableStream(payload.ToArray(), cancellation);
        var act = () => PreviewImportServiceFactory.Create().ImportFileAsync(new ImportRequest
        {
            FileStream = input,
            FileName = "canceled.parquet",
            TableName = "canceled_scratch",
            TargetSrid = 4326
        }, cancellationToken: cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        input.ScratchDirectory.Should().NotBeNull();
        Directory.Exists(input.ScratchDirectory).Should().BeFalse("owned scratch must be cleaned even before ownership reaches the outer caller");
        input.CanRead.Should().BeTrue();
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquetSeekableInput_PreservesCallerStreamOwnership()
    {
        await using var input = await GeoParquetTestFactory.CreateStreamAsync();
        await PreviewImportServiceFactory.Create().PreviewFileAsync(input, "caller-owned.parquet");
        input.CanRead.Should().BeTrue();
        input.Position = 0;
        input.ReadByte().Should().Be((byte)'P');
    }

    private sealed class CapturedNonseekableStream(byte[] bytes, CancellationTokenSource? cancelAfterCopy = null) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public string? ScratchDirectory { get; private set; }
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            ScratchDirectory = Path.GetDirectoryName(((FileStream)destination).Name);
            await base.CopyToAsync(destination, bufferSize, cancellationToken);
            cancelAfterCopy?.Cancel();
        }
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquetValidNativePointEncoding_RejectsWithExplicitCapabilityBoundary()
    {
        var x = new DataField<double>("x");
        var y = new DataField<double>("y");
        var schema = new ParquetSchema(new StructField("geometry", x, y));
        await using var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = new Dictionary<string, string>
            {
                ["geo"] = "{\"version\":\"1.1.0\",\"primary_column\":\"geometry\",\"columns\":{\"geometry\":{\"encoding\":\"point\",\"geometry_types\":[\"Point\"]}}}"
            };
            using var rows = writer.CreateRowGroup();
            await rows.WriteColumnAsync(new ParquetDataColumn(x, new[] { 1d }, definitionLevels: [1], repetitionLevels: null));
            await rows.WriteColumnAsync(new ParquetDataColumn(y, new[] { 2d }, definitionLevels: [1], repetitionLevels: null));
        }
        stream.Position = 0;
        var act = () => PreviewImportServiceFactory.Create().PreviewFileAsync(stream, "native-point.parquet");

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*native geometry encodings*supports only WKB*");
    }

    [UnitTheory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    public async Task ReadStreamingAsync_GeoParquetValidRequiredMetadata_PreservesOlderVersionAndUnknownExtensionControl(string version)
    {
        await using var stream = await CreateEdgesStreamAsync("planar", mutateMetadata: metadata =>
        {
            metadata["version"] = version;
            metadata["vendor_extension"] = new Dictionary<string, object?> { ["unrecognized"] = true };
        });
        var features = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var feature in GeoParquetReader.ReadStreamingAsync(stream, new ImportLimits(), CancellationToken.None))
        {
            features.Add(feature);
        }
        features.Should().ContainSingle();
        features[0].Geometry.Coordinates.Select(coordinate => (coordinate.X, coordinate.Y))
            .Should().Equal((179d, 10d), (-179d, 10d));
    }

    [UnitTheory]
    [InlineData("version", "missing")]
    [InlineData("version", "invalid")]
    [InlineData("encoding", "missing")]
    [InlineData("encoding", "invalid")]
    [InlineData("geometry_types", "missing")]
    [InlineData("geometry_types", "invalid")]
    [InlineData("geometry_types", "unknown")]
    [InlineData("geometry_types", "duplicate")]
    [InlineData("primary_column", "case")]
    public async Task ExtractMetadataAsync_GeoParquetRequiredMetadataInvalid_RejectsBeforeReadingRows(string key, string defect)
    {
        await using var stream = await CreateEdgesStreamAsync("planar", mutateMetadata: metadata =>
        {
            var columns = (Dictionary<string, object?>)metadata["columns"]!;
            var column = (Dictionary<string, object?>)columns["geometry"]!;
            var owner = key is "version" or "primary_column" ? metadata : column;
            if (defect == "missing")
            {
                owner.Remove(key);
            }
            else
            {
                owner[key] = defect switch
                {
                    "case" => "Geometry",
                    "unknown" => new[] { "Geometry" },
                    "duplicate" => new[] { "LineString", "LineString" },
                    _ => (object)7
                };
            }
        });

        var act = () => GeoParquetReader.ExtractMetadataAsync(stream, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [UnitTheory]
    [InlineData("identifier-only")]
    [InlineData("conflicting-definition")]
    [InlineData("missing-required-name")]
    [InlineData("nonobject")]
    public async Task ExtractMetadataAsync_GeoParquetInvalidCrs_RejectsBeforeReadingRows(string defect)
    {
        GeoParquetProjJsonCatalog.TryGetProjJson(3857, out var definition).Should().BeTrue();
        var crs = JsonNode.Parse(definition!)!.AsObject();
        if (defect == "conflicting-definition")
        {
            crs["id"]!["code"] = 4326;
        }
        else if (defect == "missing-required-name")
        {
            // PROJJSON v0.7 projected_crs requires name independently of coordinate semantics.
            crs.Remove("name");
        }
        await using var stream = await CreateEdgesStreamAsync("planar", mutateMetadata: metadata =>
        {
            var columns = (Dictionary<string, object?>)metadata["columns"]!;
            var column = (Dictionary<string, object?>)columns["geometry"]!;
            column["crs"] = defect switch
            {
                "identifier-only" => new Dictionary<string, object?>
                {
                    ["id"] = new Dictionary<string, object?> { ["authority"] = "EPSG", ["code"] = 4326 }
                },
                "conflicting-definition" or "missing-required-name" => crs,
                _ => (object)"EPSG:4326"
            };
        });

        var act = () => GeoParquetReader.ExtractMetadataAsync(stream, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [UnitTheory]
    [InlineData("version")]
    [InlineData("primary_column")]
    [InlineData("encoding")]
    public async Task ExtractMetadataAsync_GeoParquetDuplicateKnownMetadataKey_RejectsAmbiguousInput(string key)
    {
        await using var stream = await CreateEdgesStreamAsync("planar", mutateSerializedMetadata: json =>
        {
            var value = key switch { "version" => "1.1.0", "primary_column" => "geometry", _ => "WKB" };
            var token = $"\"{key}\":\"{value}\"";
            return json.Replace(token, token + "," + token, StringComparison.Ordinal);
        });
        var act = () => GeoParquetReader.ExtractMetadataAsync(stream, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*duplicate*");
    }

    [UnitTest]
    public async Task ExtractMetadataAsync_GeoParquetDuplicateNestedCrsAxisKey_RejectsOmittedCoordinateUnit()
    {
        GeoParquetProjJsonCatalog.TryGetProjJson(3857, out var definition).Should().BeTrue();
        var crs = JsonNode.Parse(definition!)!.AsObject();
        var axis = crs["coordinate_system"]!["axis"]![0]!.AsObject();
        var originalAxis = axis.ToJsonString();
        axis.Remove("unit");
        var duplicateAxis = axis.ToJsonString().Replace("\"direction\":\"east\"",
            "\"direction\":\"east\",\"direction\":\"east\"", StringComparison.Ordinal);
        duplicateAxis.Should().Contain("\"direction\":\"east\",\"direction\":\"east\"");
        var ambiguousCrs = JsonNode.Parse(definition!)!.ToJsonString()
            .Replace(originalAxis, duplicateAxis, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(ambiguousCrs);
        await using var stream = await CreateEdgesStreamAsync("planar", mutateMetadata: metadata =>
        {
            var columns = (Dictionary<string, object?>)metadata["columns"]!;
            ((Dictionary<string, object?>)columns["geometry"]!)["crs"] = document.RootElement.Clone();
        });
        var act = () => GeoParquetReader.ExtractMetadataAsync(stream, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*duplicate*");
    }

    [UnitTest]
    public async Task ExtractMetadataAsync_GeoParquetAuthoritativeProjectedCrs_PreservesResolvedSrid()
    {
        GeoParquetProjJsonCatalog.TryGetProjJson(3857, out var definition).Should().BeTrue();
        await using var stream = await CreateEdgesStreamAsync("planar", mutateMetadata: metadata =>
        {
            var columns = (Dictionary<string, object?>)metadata["columns"]!;
            ((Dictionary<string, object?>)columns["geometry"]!)["crs"] = JsonNode.Parse(definition!);
        });

        var result = await GeoParquetReader.ExtractMetadataAsync(stream, CancellationToken.None);
        result.Srid.Should().Be(3857);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadStreamingAsync_GeoParquetMeasuredWkb_RejectsExplicitUnsupportedDimensions(bool empty)
    {
        var bytes = new byte[29];
        bytes[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), 2001u);
        for (var ordinate = 0; ordinate < 3; ordinate++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(5 + ordinate * sizeof(double)), empty ? double.NaN : ordinate + 1d);
        }
        await using var stream = await CreateEdgesStreamAsync("planar", wkb: bytes);
        var act = async () =>
        {
            await foreach (var feature in GeoParquetReader.ReadStreamingAsync(stream, new ImportLimits(), CancellationToken.None))
            {
                throw new InvalidOperationException("Measured geometry is outside GeoParquet's XY/XYZ subset.");
            }
        };
        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*GeoParquet*XY*XYZ*");
    }

    [UnitTest]
    public async Task ReadStreamingAsync_GeoParquetNonlinearWkb_RejectsExplicitUnsupportedType()
    {
        var bytes = new byte[57];
        bytes[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), 8u); // SQL/MM CircularString
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), 3u);
        double[] ordinates = [0d, 0d, 1d, 1d, 2d, 0d];
        for (var i = 0; i < ordinates.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(9 + i * sizeof(double)), ordinates[i]);
        }
        await using var stream = await CreateEdgesStreamAsync("planar", wkb: bytes);
        var act = async () =>
        {
            await foreach (var feature in GeoParquetReader.ReadStreamingAsync(stream, new ImportLimits(), CancellationToken.None))
            {
                throw new InvalidOperationException("Nonlinear geometry is outside GeoParquet's XY/XYZ subset.");
            }
        };
        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*GeoParquet*XY*XYZ*");
    }

    [UnitTheory]
    [InlineData("LINESTRING (179 10, -179 10)")]
    [InlineData("POLYGON ((179 10, -179 10, -179 11, 179 11, 179 10))")]
    public async Task ImportFileAsync_GeoParquetSphericalEdges_RejectsBeforeDatabaseAccessWithWarning(string wkt)
    {
        await using var stream = await CreateEdgesStreamAsync("spherical", wkt: wkt);
        var service = PreviewImportServiceFactory.Create();

        var result = await service.ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = "spherical.parquet",
            TableName = "spherical_geoparquet",
            TargetSrid = 4326,
            OverwriteExisting = true
        });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("spherical").And.Contain("planar");
        result.Warnings.Should().Contain(warning => warning.Contains("spherical") && warning.Contains("planar"));
    }

    [UnitTheory]
    [InlineData("LINESTRING (179 10, -179 10)")]
    [InlineData("POLYGON ((179 10, -179 10, -179 11, 179 11, 179 10))")]
    public async Task PreviewFileAsync_GeoParquetSphericalEdges_RejectsBeforeSampling(string wkt)
    {
        await using var stream = await CreateEdgesStreamAsync("spherical", wkt: wkt);
        var service = PreviewImportServiceFactory.Create();

        var act = () => service.PreviewFileAsync(stream, "spherical.parquet");

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*spherical*planar*");
    }

    [UnitTheory]
    [InlineData("LINESTRING (179 10, -179 10)")]
    [InlineData("POLYGON ((179 10, -179 10, -179 11, 179 11, 179 10))")]
    public async Task ReadStreamingAsync_GeoParquetSphericalEdges_RejectsBeforeYieldingFeatures(string wkt)
    {
        await using var stream = await CreateEdgesStreamAsync("spherical", wkt: wkt);
        var act = async () =>
        {
            await foreach (var feature in GeoParquetReader.ReadStreamingAsync(stream, new ImportLimits(), CancellationToken.None))
            {
                throw new InvalidOperationException("An unsupported spherical feature must not be yielded.");
            }
        };

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*spherical*planar*");
    }

    [UnitTheory]
    [InlineData(null, true)]
    [InlineData("planar", false)]
    public async Task ReadStreamingAsync_GeoParquetPlanarEdges_PreservesAntimeridianLine(string? edges, bool omitEdges)
    {
        await using var stream = await CreateEdgesStreamAsync(edges, omitEdges);
        var features = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var feature in GeoParquetReader.ReadStreamingAsync(stream, new ImportLimits(), CancellationToken.None))
        {
            features.Add(feature);
        }

        features.Should().ContainSingle();
        features[0].Geometry.Coordinates.Select(coordinate => (coordinate.X, coordinate.Y))
            .Should().Equal((179d, 10d), (-179d, 10d));
    }

    [UnitTheory]
    [InlineData("unknown")]
    [InlineData("PLANAR")]
    [InlineData(null)]
    [InlineData(7)]
    public async Task ExtractMetadataAsync_GeoParquetInvalidEdges_RejectsMalformedMetadata(object? edges)
    {
        await using var stream = await CreateEdgesStreamAsync(edges);
        var act = () => GeoParquetReader.ExtractMetadataAsync(stream, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*edges*");
    }

    [UnitTest]
    public async Task ImportFileAsync_GeoParquetMultipleGeometryColumns_RejectsBeforeDatabaseAccess()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithSecondaryGeometryAsync();
        var service = PreviewImportServiceFactory.Create();
        var result = await service.ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = "multiple.parquet",
            TableName = "multiple_geoparquet",
            TargetSrid = 4326
        });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("multiple geometry columns");
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquetNullGeometryOnly_PreservesRowProperties()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(rowCount: 0, includeNullGeometryRow: true);
        var preview = await PreviewImportServiceFactory.Create().PreviewFileAsync(stream, "null-row.parquet");

        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties["name"].Should().Be("No Geometry");
        preview.Warnings.Should().NotContain(warning => warning.Contains("geometry was null"));
    }

    [UnitTheory]
    [InlineData("UTC", "Z")]
    [InlineData("", "")]
    public async Task ReadStreamingAsync_GeoParquetNestedNanosecondValues_PreservesRawSubTickPrecision(string timezone, string suffix)
    {
        var timestampType = new Apache.Arrow.Types.TimestampType(Apache.Arrow.Types.TimeUnit.Nanosecond, timezone);
        var timeType = new Apache.Arrow.Types.Time64Type(Apache.Arrow.Types.TimeUnit.Nanosecond);
        var fields = new[]
        {
            new Apache.Arrow.Field("timestamp", timestampType, false),
            new Apache.Arrow.Field("time", timeType, false)
        };
        var structureType = new Apache.Arrow.Types.StructType(fields);
        // Arrow C export transfers each buffer allocation exactly once. The two raw values must
        // own separate buffers even though their bytes are equal; exported arrays become invalid.
        var timestampBuffer = new Apache.Arrow.ArrowBuffer.Builder<long>().Append(123456789L).Build();
        var timeBuffer = new Apache.Arrow.ArrowBuffer.Builder<long>().Append(123456789L).Build();
        var timestamp = new Apache.Arrow.TimestampArray(timestampType, timestampBuffer, Apache.Arrow.ArrowBuffer.Empty, 1, 0, 0);
        var time = new Apache.Arrow.Time64Array(timeType, timeBuffer, Apache.Arrow.ArrowBuffer.Empty, 1, 0, 0);
        var structure = new Apache.Arrow.StructArray(structureType, 1,
            new Apache.Arrow.IArrowArray[] { timestamp, time }, Apache.Arrow.ArrowBuffer.Empty);
        var geometry = new Apache.Arrow.BinaryArray.Builder()
            .Append(new WKBWriter().Write(new NetTopologySuite.Geometries.Point(1, 2))).Build();
        var schema = new Apache.Arrow.Schema.Builder()
            .Field(new Apache.Arrow.Field("geometry", Apache.Arrow.Types.BinaryType.Default, false))
            .Field(new Apache.Arrow.Field("metadata", structureType, false))
            .Metadata(new Dictionary<string, string>
            {
                ["geo"] = "{\"version\":\"1.1.0\",\"primary_column\":\"geometry\",\"columns\":{\"geometry\":{\"encoding\":\"WKB\",\"geometry_types\":[\"Point\"]}}}"
            }).Build();
        await using var stream = new MemoryStream();
        var batch = new Apache.Arrow.RecordBatch(schema, new Apache.Arrow.IArrowArray[] { geometry, structure }, 1);
        var exportAttempted = false;
        try
        {
            using var writer = new ParquetSharp.Arrow.FileWriter(stream, schema, leaveOpen: true);
            // Export invalidates the batch even when it fails; only dispose before an attempt.
            exportAttempted = true;
            writer.WriteRecordBatch(batch);
            writer.Close();
        }
        finally
        {
            if (!exportAttempted)
            {
                batch.Dispose();
            }
        }
        stream.Position = 0;
        var rows = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var feature in GeoParquetReader.ReadStreamingAsync(stream, new ImportLimits(), CancellationToken.None))
        {
            rows.Add(feature);
        }
        rows.Should().ContainSingle();
        var metadata = (JsonElement)rows[0].Attributes["metadata"];
        metadata.GetProperty("timestamp").GetString().Should().Be("1970-01-01T00:00:00.123456789" + suffix);
        metadata.GetProperty("time").GetString().Should().Be("00:00:00.123456789");
    }

    [UnitTest]
    public async Task ImportFileAsync_GeoParquetUnsupportedMapInLaterRowGroup_RejectsBeforeDatabaseAccess()
    {
        var geometry = new DataField<byte[]>("geometry");
        var key = new DataField<int>("key", false);
        var value = new DataField<string>("value", true);
        var schema = new ParquetSchema(geometry, new MapField("mapping", key, value));
        await using var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = new Dictionary<string, string>
            {
                ["geo"] = "{\"version\":\"1.1.0\",\"primary_column\":\"geometry\",\"columns\":{\"geometry\":{\"encoding\":\"WKB\",\"geometry_types\":[\"Point\"]}}}"
            };
            var wkb = new WKBWriter().Write(new NetTopologySuite.Geometries.Point(1, 2));
            using (var first = writer.CreateRowGroup())
            {
                await first.WriteColumnAsync(new ParquetDataColumn(geometry, new[] { wkb }));
                await first.WriteColumnAsync(new ParquetDataColumn(key, Array.Empty<int>(), definitionLevels: [0], repetitionLevels: [0]));
                await first.WriteColumnAsync(new ParquetDataColumn(value, Array.Empty<string>(), definitionLevels: [0], repetitionLevels: [0]));
            }
            using (var second = writer.CreateRowGroup())
            {
                await second.WriteColumnAsync(new ParquetDataColumn(geometry, new[] { wkb }));
                await second.WriteColumnAsync(new ParquetDataColumn(key, new[] { 1 }, definitionLevels: [2], repetitionLevels: [0]));
                await second.WriteColumnAsync(new ParquetDataColumn(value, new[] { "value" }, definitionLevels: [3], repetitionLevels: [0]));
            }
        }
        stream.Position = 0;
        var result = await PreviewImportServiceFactory.Create().ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = "late-map.parquet",
            TableName = "late_unsupported_map",
            TargetSrid = 4326
        });
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("map").And.Contain("string keys");
    }

    [UnitTest]
    public async Task ReadStreamingAsync_GeoParquetNestedValues_PreservesStructListMapAndNulls()
    {
        await using var stream = await CreateNestedValuesStreamAsync();
        var features = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var feature in GeoParquetReader.ReadStreamingAsync(stream, new ImportLimits(), CancellationToken.None))
        {
            features.Add(feature);
        }

        features.Should().HaveCount(3);
        var list = features[0].Attributes["samples"].Should().BeOfType<JsonElement>().Subject;
        list.GetRawText().Should().Be("[1,null,2]");
        var map = features[0].Attributes["mapping"].Should().BeOfType<JsonElement>().Subject;
        map.GetProperty("one").GetInt32().Should().Be(1);
        map.GetProperty("two").ValueKind.Should().Be(JsonValueKind.Null);
        var structure = features[0].Attributes["metadata"].Should().BeOfType<JsonElement>().Subject;
        structure.GetProperty("key").GetString().Should().Be("alpha");
        structure.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);
        ((JsonElement)features[1].Attributes["samples"]).GetArrayLength().Should().Be(0);
        ((JsonElement)features[1].Attributes["mapping"]).EnumerateObject().Should().BeEmpty();
        ((JsonElement)features[1].Attributes["metadata"]).GetProperty("key").ValueKind.Should().Be(JsonValueKind.Null);
        features[2].Attributes["samples"].Should().BeNull();
        features[2].Attributes["mapping"].Should().BeNull();
        features[2].Attributes["metadata"].Should().BeNull();
    }

    [UnitTest]
    public async Task PreviewFileAsync_GeoParquetNativeEncoding_RejectsWithExplicitWkbOnlyContract()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(encoding: "point");
        var service = PreviewImportServiceFactory.Create();

        var act = () => service.PreviewFileAsync(stream, "native-point.parquet");

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*native geometry encodings*supports only WKB*");
    }

    [UnitTest]
    public async Task ImportFileAsync_GeoParquetNativeEncoding_ReturnsClearFailureBeforeDatabaseAccess()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(encoding: "point");
        var service = PreviewImportServiceFactory.Create();

        var result = await service.ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = "native-point.parquet",
            TableName = "native_geoparquet",
            TargetSrid = 4326,
            OverwriteExisting = true
        });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("native geometry encodings");
        result.ErrorMessage.Should().Contain("WKB");
    }
    private static async Task<MemoryStream> CreateNestedValuesStreamAsync()
    {
        var id = new DataField<long>("objectid");
        var geometry = new DataField<byte[]>("geometry");
        var item = new DataField<int?>("element");
        var list = new ListField("samples", item);
        var key = new DataField<string>("key", false);
        var value = new DataField<int?>("value");
        var map = new MapField("mapping", key, value);
        var structKey = new DataField<string>("key", true);
        var structValue = new DataField<string>("value", true);
        var structure = new StructField("metadata", structKey, structValue);
        var schema = new ParquetSchema(id, geometry, list, map, structure);
        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = new Dictionary<string, string>
            {
                ["geo"] = "{\"version\":\"1.1.0\",\"primary_column\":\"geometry\",\"columns\":{\"geometry\":{\"encoding\":\"WKB\",\"geometry_types\":[\"Point\"]}}}"
            };
            using var rows = writer.CreateRowGroup();
            var wkb = new WKBWriter().Write(new NetTopologySuite.Geometries.Point(1, 2));
            await rows.WriteColumnAsync(new ParquetDataColumn(id, new[] { 1L, 2L, 3L }));
            await rows.WriteColumnAsync(new ParquetDataColumn(geometry, new[] { wkb, wkb, wkb }));
            await rows.WriteColumnAsync(new ParquetDataColumn(item, new[] { 1, 2 }, definitionLevels: [3, 2, 3, 1, 0], repetitionLevels: [0, 1, 1, 0, 0]));
            await rows.WriteColumnAsync(new ParquetDataColumn(key, new[] { "one", "two" }, definitionLevels: [2, 2, 1, 0], repetitionLevels: [0, 1, 0, 0]));
            await rows.WriteColumnAsync(new ParquetDataColumn(value, new[] { 1 }, definitionLevels: [3, 2, 1, 0], repetitionLevels: [0, 1, 0, 0]));
            await rows.WriteColumnAsync(new ParquetDataColumn(structKey, new[] { "alpha" }, definitionLevels: [2, 1, 0], repetitionLevels: null));
            await rows.WriteColumnAsync(new ParquetDataColumn(structValue, Array.Empty<string>(), definitionLevels: [1, 1, 0], repetitionLevels: null));
        }
        stream.Position = 0;
        return stream;
    }

    private static async Task<MemoryStream> CreateEdgesStreamAsync(
        object? edges,
        bool omitEdges = false,
        string? secondaryEdges = null,
        string wkt = "LINESTRING (179 10, -179 10)",
        Action<Dictionary<string, object?>>? mutateMetadata = null,
        byte[]? wkb = null,
        Func<string, string>? mutateSerializedMetadata = null)
    {
        var id = new DataField<long>("objectid");
        var geometry = new DataField<byte[]>("geometry");
        var secondary = new DataField<byte[]>("geometry2");
        var schema = secondaryEdges is null
            ? new ParquetSchema(id, geometry)
            : new ParquetSchema(id, geometry, secondary);
        var primaryMetadata = new Dictionary<string, object?>
        {
            ["encoding"] = "WKB",
            ["geometry_types"] = Array.Empty<string>()
        };
        if (!omitEdges)
        {
            primaryMetadata["edges"] = edges;
        }
        var columns = new Dictionary<string, object?> { ["geometry"] = primaryMetadata };
        if (secondaryEdges is not null)
        {
            columns["geometry2"] = new Dictionary<string, object?>
            {
                ["encoding"] = "WKB",
                ["geometry_types"] = Array.Empty<string>(),
                ["edges"] = secondaryEdges
            };
        }
        var geo = new Dictionary<string, object?>
        {
            ["version"] = "1.1.0",
            ["primary_column"] = "geometry",
            ["columns"] = columns
        };
        mutateMetadata?.Invoke(geo);
        var metadata = JsonSerializer.Serialize(geo);
        metadata = mutateSerializedMetadata?.Invoke(metadata) ?? metadata;
        var bytes = wkb ?? new WKBWriter().Write(new WKTReader().Read(wkt));
        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = new Dictionary<string, string> { ["geo"] = metadata };
            using var rowGroup = writer.CreateRowGroup();
            await rowGroup.WriteColumnAsync(new ParquetDataColumn(id, new[] { 1L }));
            await rowGroup.WriteColumnAsync(new ParquetDataColumn(geometry, new[] { bytes }));
            if (secondaryEdges is not null)
            {
                await rowGroup.WriteColumnAsync(new ParquetDataColumn(secondary, new[] { bytes }));
            }
        }
        stream.Position = 0;
        return stream;
    }

}
