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

        var ok = GeometryExecutorSupport.TryReadSingleGeometryInputs(parameters, out var inputs, out var error);

        ok.Should().BeTrue();
        error.Should().BeEmpty();
        inputs.Srid.Should().Be(4326);
        inputs.WkbBytes.Should().Equal(wkb);
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

        var areaOk = GeometryExecutorSupport.TryReadMeasuredGeometryInputs(areaParams, isArea: true, out var areaInputs, out var areaError);
        areaOk.Should().BeTrue();
        areaError.Should().BeEmpty();
        areaInputs.Geodesic.Should().BeTrue();
        areaInputs.Unit.Should().Be("hectares");
        areaInputs.MetersPerUnit.Should().Be(10_000d);

        var badLengthParams = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Prefix + "wkb"] = wkb,
            [Prefix + "srid"] = "4326",
            [Prefix + "unit"] = "furlongs",
        };

        var lengthOk = GeometryExecutorSupport.TryReadMeasuredGeometryInputs(badLengthParams, isArea: false, out _, out var lengthError);
        lengthOk.Should().BeFalse();
        lengthError.Should().Contain("furlongs");
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

        GeometryExecutorSupport.TryReadPairGeometryInputs(parameters, "targetWkb", "clipEnvelopeWkb", out var pair, out var pairError)
            .Should().BeTrue();
        pairError.Should().BeEmpty();
        pair.Srid.Should().Be(3857);

        GeometryExecutorSupport.TryDecodeGeometry(pair.FirstWkb, out var geometry, out var decodeError)
            .Should().BeTrue();
        decodeError.Should().BeEmpty();
        geometry.Should().BeOfType<Point>();

        GeometryExecutorSupport.TryDecodeGeometry("not-base64!!!", out _, out var badDecodeError)
            .Should().BeFalse();
        badDecodeError.Should().Be("is not valid base64");
    }

    [UnitTest]
    public void TryDecodeWkbAndValidateSrid_MismatchedEmbeddedSrid_ReportsSridError()
    {
        var wkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: true).Write(new Point(1, 2) { SRID = 3857 });

        var ok = GeometryExecutorSupport.TryDecodeWkbAndValidateSrid(
            wkb,
            4326,
            out _,
            out var wkbError,
            out var isEmpty,
            out var sridError);

        ok.Should().BeFalse();
        wkbError.Should().BeNull();
        isEmpty.Should().BeFalse();
        sridError.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void BuildMeasurePayload_AndBuildJsonDataUri_ProduceExpectedEnvelope()
    {
        var payload = GeometryExecutorSupport.BuildMeasurePayload(
            "geometry.area",
            "area",
            42.5,
            "square-meters",
            4326,
            "Polygon");

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
        static string Serialize(object? value, bool writeNonFiniteAsNull)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                SinkFeatureEncoder.WriteAttribute(writer, "v", value, writeNonFiniteAsNull);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        Serialize(double.NaN, writeNonFiniteAsNull: true).Should().Be("{\"v\":null}");
        Serialize(float.PositiveInfinity, writeNonFiniteAsNull: true).Should().Be("{\"v\":null}");
        Serialize(double.NaN, writeNonFiniteAsNull: false).Should().Be("{\"v\":\"NaN\"}");
        Serialize(12.5, writeNonFiniteAsNull: true).Should().Be("{\"v\":12.5}");
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
        reader.ReadBool("dissolve", defaultValue: true).Should().BeFalse();
        reader.ReadBool("missingFlag", defaultValue: true).Should().BeTrue();
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
