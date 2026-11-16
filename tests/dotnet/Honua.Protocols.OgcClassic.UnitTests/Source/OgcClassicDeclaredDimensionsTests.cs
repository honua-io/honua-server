// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Classic;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic;

public sealed class OgcClassicDeclaredDimensionsTests
{
    private static MetadataV2Object ResourceWith(string key, string json)
    {
        using var document = JsonDocument.Parse(json);
        return new MetadataV2Object
        {
            Extensions = new Dictionary<string, JsonElement> { [key] = document.RootElement.Clone() }
        };
    }

    [UnitTest]
    public void ReadWmsDimensions_ReadsEveryDeclaredField()
    {
        var resource = ResourceWith("wms", """
            {"dimensions":[{"name":"elevation","units":"CRS:88","unitSymbol":"m","multipleValues":true,
              "nearestValue":true,"default":"0/425","current":"425","extent":"0/425/1"}]}
            """);

        OgcClassicDeclaredDimensions.ReadWmsDimensions(resource).Should().Equal(
            new DeclaredWmsDimension
            {
                Name = "elevation",
                Units = "CRS:88",
                UnitSymbol = "m",
                MultipleValues = true,
                NearestValue = true,
                Default = "0/425",
                Current = "425",
                Extent = "0/425/1"
            });
        OgcClassicDeclaredDimensions.HasWmsTimeDimension(resource).Should().BeFalse();
    }

    [UnitTest]
    public void ReadWmsDimensions_AppliesDefaultsAndKeepsFirstOfDuplicateNames()
    {
        var resource = ResourceWith("wms", """
            {"dimensions":[{"name":"TIME","units":"ISO8601","extent":"2000/2001/P1Y"},{"name":"time","units":"other"}]}
            """);

        OgcClassicDeclaredDimensions.ReadWmsDimensions(resource).Should().Equal(
            new DeclaredWmsDimension { Name = "TIME", Units = "ISO8601", Extent = "2000/2001/P1Y" });
        OgcClassicDeclaredDimensions.HasWmsTimeDimension(resource).Should().BeTrue();
    }

    [UnitTest]
    public void ReadWmtsDimensions_ReadsValuesIncludingUnquotedNumbers()
    {
        var resource = ResourceWith("wmts", """
            {"dimensions":[{"identifier":"elevation","values":[100,"200"],"default":100,"supportsCurrent":true,"current":"200"}]}
            """);

        var dimension = OgcClassicDeclaredDimensions.ReadWmtsDimensions(resource).Should().ContainSingle().Subject;

        dimension.Identifier.Should().Be("elevation");
        dimension.Values.Should().Equal("100", "200");
        dimension.DefaultValue.Should().Be("100");
        dimension.SupportsCurrent.Should().BeTrue();
        dimension.CurrentValue.Should().Be("200");
    }

    [UnitTheory]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"dimensions\":{\"name\":\"time\"}}")]
    [InlineData("{\"dimensions\":[null,3,\"time\",[],{},{\"name\":7},{\"name\":\" \"},{\"identifier\":\"e\"},{\"identifier\":\"e\",\"values\":[]},{\"identifier\":\"e\",\"values\":\"1\"}]}")]
    public void MalformedExtensions_DeclareNothing(string json)
    {
        var wms = ResourceWith("wms", json);
        var wmts = ResourceWith("wmts", json);

        OgcClassicDeclaredDimensions.ReadWmsDimensions(wms).Should().BeEmpty();
        OgcClassicDeclaredDimensions.HasWmsTimeDimension(wms).Should().BeFalse();
        OgcClassicDeclaredDimensions.ReadWmtsDimensions(wmts).Should().BeEmpty();
        OgcClassicDeclaredDimensions.IsWmtsQueryable(wmts).Should().BeTrue();
    }

    [UnitTest]
    public void ReadWmtsDimensions_IgnoresTheReservedTimeIdentifier()
    {
        var resource = ResourceWith("wmts", """{"dimensions":[{"identifier":"Time","values":["2020"]},{"identifier":"elevation","values":["1"]}]}""");

        OgcClassicDeclaredDimensions.ReadWmtsDimensions(resource).Should().ContainSingle()
            .Which.Identifier.Should().Be("elevation");
    }

    [UnitTest]
    public void IsWmtsQueryable_IsFalseForAnExplicitFalse()
        => OgcClassicDeclaredDimensions.IsWmtsQueryable(ResourceWith("wmts", """{"queryable":false}""")).Should().BeFalse();

    [UnitTheory]
    [InlineData("{\"queryable\":true}")]
    [InlineData("{\"queryable\":\"false\"}")]
    [InlineData("{\"dimensions\":[]}")]
    public void IsWmtsQueryable_IsTrueForAnythingElse(string json)
        => OgcClassicDeclaredDimensions.IsWmtsQueryable(ResourceWith("wmts", json)).Should().BeTrue();

    [UnitTest]
    public void ResourceWithoutExtensions_DeclaresNothing()
    {
        var resource = new MetadataV2Object();

        OgcClassicDeclaredDimensions.ReadWmsDimensions(resource).Should().BeEmpty();
        OgcClassicDeclaredDimensions.ReadWmtsDimensions(resource).Should().BeEmpty();
        OgcClassicDeclaredDimensions.IsWmtsQueryable(resource).Should().BeTrue();
    }
}
