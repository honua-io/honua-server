// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Db.Postgres.Tests.Features.Import;

[Trait("Category", "Unit")]
[Trait("Tier", "Fast")]
public sealed class StreamingGeoJsonNullGeometryTests
{
    [Fact]
    public async Task ValidateAsync_NullGeometry_AcceptsFeatureWithIndexedWarning()
    {
        await using var stream = FeatureStream("\"geometry\":null,");
        var reader = new StreamingGeoJsonReader();

        var result = await reader.ValidateAsync(stream);

        result.IsValid.Should().BeTrue();
        var warning = result.Issues.Should().ContainSingle().Subject;
        warning.Severity.Should().Be(ImportValidationSeverity.Warning);
        warning.FeatureIndex.Should().Be(1);
        warning.Field.Should().Be("geometry");
        warning.Message.Should().Contain("null geometry");
        var features = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var item in reader.ReadFeaturesAsync(stream)) features.Add(item);
        var feature = features.Should().ContainSingle().Subject;
        feature.Geometry.Should().BeNull();
        feature.Attributes["name"].Should().Be("unlocated");
    }

    [Theory]
    [InlineData("", ImportValidationErrorCodes.GeometryMissing)]
    [InlineData("\"geometry\":42,", ImportValidationErrorCodes.GeometryInvalid)]
    [InlineData("\"geometry\":{},", ImportValidationErrorCodes.GeometryMissing)]
    [InlineData("\"geometry\":{\"type\":\"Unknown\"},", ImportValidationErrorCodes.GeometryUnknownType)]
    public async Task ValidateAsync_MissingOrMalformedGeometry_StillRejectsFeature(string geometry, string errorCode)
    {
        await using var stream = FeatureStream(geometry);

        var result = await new StreamingGeoJsonReader().ValidateAsync(stream);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Code == errorCode &&
            issue.Severity == ImportValidationSeverity.Error && issue.FeatureIndex == 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"geometry\":{},")]
    public async Task ValidateAsync_InvalidFeatureAfterWarningLimit_StillRejectsFile(string geometry)
    {
        var features = Enumerable.Repeat(
            "{\"type\":\"Feature\",\"geometry\":null,\"properties\":{}}", 100)
            .Append("{\"type\":\"Feature\"," + geometry + "\"properties\":{}}");
        var json = "{\"type\":\"FeatureCollection\",\"features\":[" + string.Join(',', features) + "]}";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await new StreamingGeoJsonReader().ValidateAsync(stream);

        result.IsValid.Should().BeFalse();
        result.FeatureCount.Should().Be(101);
        result.Issues.Should().HaveCount(100);
        result.Issues.Should().Contain(issue => issue.FeatureIndex == 101 &&
            issue.Severity == ImportValidationSeverity.Error);
    }

    private static MemoryStream FeatureStream(string geometry) => new(Encoding.UTF8.GetBytes(
        $$$"""{"type":"FeatureCollection","features":[{"type":"Feature",{{{geometry}}}"properties":{"name":"unlocated"}}]}"""));
}
