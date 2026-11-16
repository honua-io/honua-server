// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed class WfsDeclaredEncodingTests
{
    private static WfsDeclaredEncoding Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return WfsDeclaredEncoding.Read(new MetadataV2Object
        {
            Extensions = new Dictionary<string, JsonElement> { ["wfs"] = document.RootElement.Clone() }
        });
    }

    [UnitTest]
    public void Read_ReturnsTheDeclaredNamespaceAndEncoding()
    {
        var declared = Read("""
            {"namespace":{"prefix":"cdf","uri":"http://www.opengis.net/cite/data"},
             "legacyEncoding":{"omitNullProperties":true,"geometryAfterAttributes":true,
               "hiddenFields":["objectid","name"],"gmlFields":["pointProperty"],"wfs10Schema":"<xsd/>",
               "wfs10BoundedBy":{"srsName":"EPSG:32615","coordinates":"0,0 1,1"}}}
            """);

        declared.NamespacePrefix.Should().Be("cdf");
        declared.NamespaceUri.Should().Be("http://www.opengis.net/cite/data");
        declared.OmitNullProperties.Should().BeTrue();
        declared.GeometryAfterAttributes.Should().BeTrue();
        declared.IsHidden("OBJECTID").Should().BeTrue();
        declared.IsHidden("string1").Should().BeFalse();
        declared.IsGmlField("pointProperty").Should().BeTrue();
        declared.Wfs10Schema.Should().Be("<xsd/>");
        declared.Wfs10BoundedBy.Should().Be(new WfsDeclaredBoundedBy("EPSG:32615", "0,0 1,1"));
    }

    [UnitTheory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"namespace\":\"cdf\",\"legacyEncoding\":[]}")]
    [InlineData("{\"namespace\":{\"prefix\":\"cdf\"}}")]
    [InlineData("{\"namespace\":{\"prefix\":\"cdf\",\"uri\":\"not a uri\"}}")]
    [InlineData("{\"namespace\":{\"prefix\":\"a b\",\"uri\":\"https://example.test/a\"}}")]
    [InlineData("{\"namespace\":{\"prefix\":\"a:b\",\"uri\":\"https://example.test/a\"}}")]
    [InlineData("{\"namespace\":{\"prefix\":\"gml\",\"uri\":\"https://example.test/a\"}}")]
    [InlineData("{\"namespace\":{\"prefix\":\"WFS\",\"uri\":\"https://example.test/a\"}}")]
    [InlineData("{\"namespace\":{\"prefix\":7,\"uri\":\"https://example.test/a\"}}")]
    public void Read_MalformedOrReservedNamespace_DeclaresNothing(string json)
        => Read(json).Should().BeSameAs(WfsDeclaredEncoding.None);

    [UnitTest]
    public void Read_MalformedEncodingMembers_AreIgnoredOneByOne()
    {
        var declared = Read("""
            {"legacyEncoding":{"omitNullProperties":"true","hiddenFields":[1,null,"  ","name"],"gmlFields":"x",
               "wfs10Schema":7,"wfs10BoundedBy":{"srsName":"EPSG:4326"}}}
            """);

        declared.NamespacePrefix.Should().BeNull();
        declared.OmitNullProperties.Should().BeFalse();
        declared.HiddenFields.Should().Equal("name");
        declared.GmlFields.Should().BeEmpty();
        declared.Wfs10Schema.Should().BeNull();
        declared.Wfs10BoundedBy.Should().BeNull();
    }

    [UnitTest]
    public void Read_ResourceWithoutExtension_DeclaresNothing()
        => WfsDeclaredEncoding.Read(new MetadataV2Object()).Should().BeSameAs(WfsDeclaredEncoding.None);

    [UnitTest]
    public void WithoutNamespace_KeepsTheEncoding()
    {
        var declared = Read("""{"namespace":{"prefix":"ext","uri":"https://example.test/a"},"legacyEncoding":{"omitNullProperties":true}}""")
            .WithoutNamespace();

        declared.NamespacePrefix.Should().BeNull();
        declared.NamespaceUri.Should().BeNull();
        declared.OmitNullProperties.Should().BeTrue();
    }
}
