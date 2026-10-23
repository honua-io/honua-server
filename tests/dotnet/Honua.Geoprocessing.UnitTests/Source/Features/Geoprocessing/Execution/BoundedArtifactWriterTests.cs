// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed class BoundedArtifactWriterTests
{
    [UnitTest]
    public void Write_AtExactUtf8Boundary_SucceedsAndOneByteLessFails()
    {
        const string expected = "{\"type\":\"FeatureCollection\",\"processId\":\"proof\",\"featureCount\":0,\"features\":[]}";
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        BoundedArtifactWriter.WriteFeatureCollection([], "proof", expectedBytes.Length, CancellationToken.None)
            .Should().Equal(expectedBytes);
        Action over = () => BoundedArtifactWriter.WriteFeatureCollection([], "proof", expectedBytes.Length - 1, CancellationToken.None);
        over.Should().Throw<TransformInputException>().WithMessage("*stopped during serialization*");
    }

    [UnitTest]
    public void Write_PointWithZUnicodeNullAndInt64_PreservesValuesAndMetadata()
    {
        var attributes = new AttributesTable
        {
            { "name", "Kīlauea 日本" }, { "missing", null! }, { "serial", 9007199254740993L }
        };
        var feature = new Feature(new Point(new CoordinateZ(1, 2, -3.25)), attributes);
        var bytes = BoundedArtifactWriter.WriteFeatureCollection([feature], "proof", 1024, CancellationToken.None, [("srid", 4326)]);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        root.GetProperty("featureCount").GetInt32().Should().Be(1);
        root.GetProperty("processId").GetString().Should().Be("proof");
        root.GetProperty("srid").GetInt32().Should().Be(4326);
        var result = root.GetProperty("features")[0];
        result.GetProperty("geometry").GetProperty("coordinates").EnumerateArray().Select(v => v.GetDouble())
            .Should().Equal(1, 2, -3.25);
        result.GetProperty("properties").GetProperty("name").GetString().Should().Be("Kīlauea 日本");
        result.GetProperty("properties").GetProperty("missing").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("properties").GetProperty("serial").GetInt64().Should().Be(9007199254740993L);
    }

    [UnitTest]
    public void Write_OversizedAttribute_StopsBeforeAccessingAnotherFeature()
    {
        var large = new Feature(new Point(1, 2), new AttributesTable { { "text", new string('界', 100_000) } });
        var later = Substitute.For<IFeature>();
        later.Geometry.Returns(_ => throw new InvalidOperationException("read beyond budget"));
        Action write = () => BoundedArtifactWriter.WriteFeatureCollection([large, later], "proof", 1024, CancellationToken.None);
        write.Should().Throw<TransformInputException>().WithMessage("*MaxArtifactBytes=1024*stopped during serialization*");
        _ = later.DidNotReceive().Geometry;
    }

    [UnitTest]
    public void Write_Cancelled_StopsWithoutReadingFeatures()
    {
        var feature = Substitute.For<IFeature>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Action write = () => BoundedArtifactWriter.WriteFeatureCollection([feature], "proof", 1024, cancelled.Token);
        write.Should().Throw<OperationCanceledException>();
        _ = feature.DidNotReceive().Geometry;
    }
}
