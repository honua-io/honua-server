// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed class GeometryExecutorSupportTests
{
    private static readonly string Prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

    [UnitTest]
    public void TryReadSingleGeometryInputs_ValidParameters_DecodesWkbAndSrid()
    {
        var wkb = new WKBWriter().Write(new Point(1, 2) { SRID = 4326 });
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Prefix + "wkb"] = Convert.ToBase64String(wkb),
            [Prefix + "srid"] = "4326",
        };

        var read = GeometryExecutorSupport.ReadSingleGeometryInputs(parameters);

        read.IsValid.Should().BeTrue();
        read.Error.Should().BeNull();
        read.Inputs.Srid.Should().Be(4326);
        read.Inputs.WkbBytes.Should().Equal(wkb);
    }

    [UnitTest]
    public void TryReadMeasuredGeometryInputs_AreaAndLengthUnits_ValidateAndNormalize()
    {
        var wkb = Convert.ToBase64String(new WKBWriter().Write(new Point(0, 0) { SRID = 4326 }));

        var areaParams = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Prefix + "wkb"] = wkb,
            [Prefix + "srid"] = "4326",
            [Prefix + "geodesic"] = "true",
            [Prefix + "unit"] = "hectares",
        };

        var areaRead = GeometryExecutorSupport.ReadAreaGeometryInputs(areaParams);
        areaRead.IsValid.Should().BeTrue();
        areaRead.Error.Should().BeNull();
        areaRead.Inputs.Mode.Should().Be(MeasurementMode.Geodesic);
        areaRead.Inputs.Unit.Should().Be("hectares");
        areaRead.Inputs.MetersPerUnit.Should().Be(10_000d);

        var badLengthParams = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Prefix + "wkb"] = wkb,
            [Prefix + "srid"] = "4326",
            [Prefix + "unit"] = "furlongs",
        };

        var lengthRead = GeometryExecutorSupport.ReadLengthGeometryInputs(badLengthParams);
        lengthRead.IsValid.Should().BeFalse();
        lengthRead.Error.Should().Contain("furlongs");
    }

    [UnitTest]
    public void TryReadPairGeometryInputs_AndTryDecodeGeometry_ValidateAndDecode()
    {
        var wkb = Convert.ToBase64String(new WKBWriter().Write(new Point(3, 4)));
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Prefix + "targetWkb"] = wkb,
            [Prefix + "clipEnvelopeWkb"] = wkb,
            [Prefix + "srid"] = "3857",
        };

        var pairRead = GeometryExecutorSupport.ReadPairGeometryInputs(
            parameters,
            new PairGeometryInputNames("targetWkb", "clipEnvelopeWkb"));
        pairRead.IsValid.Should().BeTrue();
        pairRead.Error.Should().BeNull();
        pairRead.Inputs.Srid.Should().Be(3857);

        var decode = GeometryExecutorSupport.DecodeGeometry(pairRead.Inputs.FirstWkb);
        decode.IsValid.Should().BeTrue();
        decode.Error.Should().BeNull();
        decode.Geometry.Should().BeOfType<Point>();

        var badDecode = GeometryExecutorSupport.DecodeGeometry("not-base64!!!");
        badDecode.IsValid.Should().BeFalse();
        badDecode.Error.Should().Be("is not valid base64");
    }

    [UnitTest]
    public void TryDecodeWkbAndValidateSrid_MismatchedEmbeddedSrid_ReportsSridError()
    {
        var wkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: true).Write(new Point(1, 2) { SRID = 3857 });

        var decode = GeometryExecutorSupport.DecodeWkbAndValidateSrid(wkb, 4326);

        decode.IsValid.Should().BeFalse();
        decode.FailureKind.Should().Be(WkbDecodeFailureKind.InvalidSrid);
        decode.Error.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void BuildMeasurePayload_AndBuildJsonDataUri_ProduceExpectedEnvelope()
    {
        var payload = GeometryExecutorSupport.BuildMeasurePayload(new MeasurePayloadRequest
        {
            ProcessId = "geometry.area",
            Measure = "area",
            Value = 42.5,
            Unit = "square-meters",
            InputSrid = 4326,
            InputGeometryType = "Polygon"
        });

        var uri = GeometryExecutorSupport.BuildJsonDataUri(payload);
        uri.Should().StartWith("data:application/json;base64,");

        var decoded = Convert.FromBase64String(uri["data:application/json;base64,".Length..]);
        using var doc = JsonDocument.Parse(decoded);
        doc.RootElement.GetProperty("type").GetString().Should().Be("MeasureResult");
        doc.RootElement.GetProperty("processId").GetString().Should().Be("geometry.area");
        doc.RootElement.GetProperty("measure").GetString().Should().Be("area");
        doc.RootElement.GetProperty("value").GetDouble().Should().Be(42.5);
        doc.RootElement.GetProperty("unit").GetString().Should().Be("square-meters");
        doc.RootElement.GetProperty("inputSrid").GetInt32().Should().Be(4326);
        doc.RootElement.GetProperty("inputGeometryType").GetString().Should().Be("Polygon");
    }

    [UnitTest]
    public void SinkFeatureEncoder_WriteAttribute_RespectsWriteNonFiniteAsNull()
    {
        SerializeAttribute(double.NaN, NonFiniteMode.AsNull).Should().Be("{\"v\":null}");
        SerializeAttribute(float.PositiveInfinity, NonFiniteMode.AsNull).Should().Be("{\"v\":null}");
        SerializeAttribute(double.NaN, NonFiniteMode.AsString).Should().Be("{\"v\":\"NaN\"}");
        SerializeAttribute(12.5, NonFiniteMode.AsNull).Should().Be("{\"v\":12.5}");
    }

    private enum NonFiniteMode
    {
        AsNull,
        AsString,
    }

    private static string SerializeAttribute(object? value, NonFiniteMode mode)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (mode == NonFiniteMode.AsNull)
            {
                SinkFeatureEncoder.WriteAttributeWithNullNonFinite(writer, "v", value);
            }
            else
            {
                SinkFeatureEncoder.WriteAttribute(writer, "v", value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [UnitTest]
    public void SinkFeatureEncoder_Identifier_AcceptsValidAndRejectsInvalid()
    {
        SinkFeatureEncoder.Identifier("valid_table_1").Should().Be("valid_table_1");

        var act = () => SinkFeatureEncoder.Identifier("bad-table;drop");
        act.Should().Throw<TransformInputException>();
    }

    [UnitTest]
    public void StepInputReader_SharedHelpers_ParseSridBoolDistanceAndUnitFactor()
    {
        var reader = new StepInputReader(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Prefix + "targetSrid"] = "4326",
            [Prefix + "dissolve"] = "no",
            [Prefix + "distance"] = "12.5",
            [Prefix + "unit"] = "km",
        });

        reader.RequireSrid("targetSrid").Should().Be(4326);
        reader.ReadBoolDefaultTrue("dissolve").Should().BeFalse();
        reader.ReadBoolDefaultTrue("missingFlag").Should().BeTrue();
        reader.ReadDistance().Should().Be(12.5);
        reader.ReadUnitFactor().Should().Be(1000.0);
    }

    [UnitTest]
    public void StatisticsSupport_EscapeKeyComponent_EscapesControlCharacters()
    {
        StatisticsSupport.EscapeKeyComponent("plain").Should().Be("plain");
        StatisticsSupport.EscapeKeyComponent("a\u001Eb\u001Fc").Should().Be("a\u001E\u001Eb\u001E\u001Fc");
    }

    [UnitTest]
    public async Task FeatureStreamArtifact_ToAsync_StreamsBothCollectionAndList()
    {
        var feature = new Feature(new Point(1, 2), new AttributesTable { { "id", 1 } });
        var collection = new FeatureCollection { feature };
        var list = new List<IFeature> { feature };

        var fromCollection = new List<IFeature>();
        await foreach (var item in FeatureStreamArtifact.ToAsync(collection, CancellationToken.None))
        {
            fromCollection.Add(item);
        }

        var fromList = new List<IFeature>();
        await foreach (var item in FeatureStreamArtifact.ToAsync(list, CancellationToken.None))
        {
            fromList.Add(item);
        }

        fromCollection.Should().ContainSingle().Which.Should().BeSameAs(feature);
        fromList.Should().ContainSingle().Which.Should().BeSameAs(feature);
    }

    [UnitTest]
    public void FileSinkInputReader_TryDeleteTempFile_DeletesExistingAndIgnoresMissing()
    {
        var tempPath = Path.Join(Path.GetTempPath(), $"honua-gp-temp-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, "temp");

        FileSinkInputReader.TryDeleteTempFile(tempPath);
        File.Exists(tempPath).Should().BeFalse();

        var act = () => FileSinkInputReader.TryDeleteTempFile(tempPath);
        act.Should().NotThrow();
    }
}
