// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Raster.Functions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Protocols.GeoServices.Tests.Source.ImageServer;

public sealed class ImageServerRasterFunctionDefinitionAdapterTests
{
    private static RasterFunctionDocument Parse(string json)
        => JsonSerializer.Deserialize(json, ImageServerJsonContext.Default.RasterFunctionDocument)!;

    private static RasterFunctionDocument CreateIdentityChain(int depth)
    {
        var json = "{\"rasterFunction\":\"Identity\"}";
        for (var index = 1; index < depth; index++)
        {
            json = "{\"rasterFunction\":\"Identity\",\"rasterFunctionArguments\":{\"Raster\":" + json + "}}";
        }

        return Parse(json);
    }

    [UnitTest]
    public void Adapt_LinearChain_PreservesInnerToOuterSemanticOrder()
    {
        var document = Parse(
            """
            {
              "rasterFunction":"Colormap",
              "rasterFunctionArguments":{
                "Colormap":[[0,0,0,0],[1,255,255,255]],
                "Raster":{
                  "rasterFunction":"Stretch",
                  "rasterFunctionArguments":{
                    "StretchType":5,
                    "Raster":{
                      "rasterFunction":"BandArithmetic",
                      "rasterFunctionArguments":{"Method":3,"BandIndexes":[2,3]}
                    }
                  }
                }
              }
            }
            """);

        var mapping = ImageServerRasterFunctionDefinitionAdapter.Adapt(document);

        mapping.Supported.Should().BeTrue();
        var definition = mapping.Definition!;
        definition.OutputNodeId.Should().Be("function-3");
        definition.Nodes.Should().HaveCount(4);

        var source = definition.Nodes[0].Should().BeOfType<RasterFunctionInputNode>().Subject;
        source.Id.Should().Be("input");
        source.InputName.Should().Be("raster");

        var index = definition.Nodes[1].Should().BeOfType<RasterFunctionSpectralIndexNode>().Subject;
        index.Id.Should().Be("function-1");
        index.Inputs.Should().Equal("input");
        index.Method.Should().Be(RasterSpectralIndexMethod.Ndvi);
        index.PrimaryBand.Should().Be(4);
        index.SecondaryBand.Should().Be(3);

        var stretch = definition.Nodes[2].Should().BeOfType<RasterFunctionStretchNode>().Subject;
        stretch.Id.Should().Be("function-2");
        stretch.Inputs.Should().Equal("function-1");
        stretch.Stretch.StretchType.Should().Be(RasterStretchType.MinMax);

        var colormap = definition.Nodes[3].Should().BeOfType<RasterFunctionColormapNode>().Subject;
        colormap.Id.Should().Be("function-3");
        colormap.Inputs.Should().Equal("function-2");
    }

    [UnitTest]
    public void Adapt_IdentityAndExtractBand_ProduceCanonicalNodes()
    {
        var identity = ImageServerRasterFunctionDefinitionAdapter.Adapt(Parse(
            """{"rasterFunction":"Identity"}"""));
        var extract = ImageServerRasterFunctionDefinitionAdapter.Adapt(Parse(
            """{"rasterFunction":"ExtractBand","rasterFunctionArguments":{"BandIds":[0,2]}}"""));

        identity.Supported.Should().BeTrue();
        identity.Definition!.Nodes[1].Should().BeOfType<RasterFunctionIdentityNode>();

        extract.Supported.Should().BeTrue();
        var selection = extract.Definition!.Nodes[1].Should().BeOfType<RasterFunctionBandSelectNode>().Subject;
        selection.Bands.Should().Equal(1, 3);
    }

    [UnitTest]
    public void Adapt_ClosedBandArithmeticMethods_ProduceCanonicalSpectralIndexes()
    {
        var cases = new[]
        {
            (Method: 3, Expected: RasterSpectralIndexMethod.Ndvi, Primary: 4, Secondary: 3),
            (Method: 5, Expected: RasterSpectralIndexMethod.Savi, Primary: 4, Secondary: 3),
            (Method: 9, Expected: RasterSpectralIndexMethod.Ndwi, Primary: 3, Secondary: 4),
        };

        foreach (var item in cases)
        {
            var document = Parse(
                $"{{\"rasterFunction\":\"BandArithmetic\",\"rasterFunctionArguments\":{{\"Method\":{item.Method},\"BandIndexes\":[2,3]}}}}");

            var mapping = ImageServerRasterFunctionDefinitionAdapter.Adapt(document);

            mapping.Supported.Should().BeTrue();
            var index = mapping.Definition!.Nodes[1].Should().BeOfType<RasterFunctionSpectralIndexNode>().Subject;
            index.Method.Should().Be(item.Expected);
            index.PrimaryBand.Should().Be(item.Primary);
            index.SecondaryBand.Should().Be(item.Secondary);
        }
    }

    [UnitTest]
    public void Adapt_Clip_ProducesCanonicalWkbRegion()
    {
        var document = Parse(
            """
            {"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingType":1,"Extent":{"xmin":0,"ymin":1,"xmax":2,"ymax":3,"spatialReference":{"wkid":4326}}}}
            """);

        var mapping = ImageServerRasterFunctionDefinitionAdapter.Adapt(document);

        mapping.Supported.Should().BeTrue();
        var clip = mapping.Definition!.Nodes[1].Should().BeOfType<RasterFunctionClipNode>().Subject;
        clip.Region.Geometry.Should().NotBeEmpty();
        clip.Region.Srid.Should().Be(4326);
        clip.Region.Inverted.Should().BeTrue();
    }

    [UnitTest]
    public void Adapt_TerrainFunctions_ProduceClosedCanonicalNodes()
    {
        var cases = new[]
        {
            (Function: "Hillshade", Method: RasterTerrainMethod.Hillshade),
            (Function: "Slope", Method: RasterTerrainMethod.Slope),
            (Function: "Aspect", Method: RasterTerrainMethod.Aspect),
        };

        foreach (var item in cases)
        {
            var mapping = ImageServerRasterFunctionDefinitionAdapter.Adapt(Parse(
                $"{{\"rasterFunction\":\"{item.Function}\",\"rasterFunctionArguments\":{{\"BandId\":1,\"ZFactor\":2}}}}"));

            mapping.Supported.Should().BeTrue();
            var terrain = mapping.Definition!.Nodes[1].Should().BeOfType<RasterFunctionTerrainNode>().Subject;
            terrain.Terrain.Method.Should().Be(item.Method);
            terrain.Terrain.Band.Should().Be(2);
            terrain.Terrain.ZFactor.Should().Be(2);
        }
    }

    [UnitTest]
    public void Adapt_StretchNone_ProducesExplicitCanonicalNoOp()
    {
        var mapping = ImageServerRasterFunctionDefinitionAdapter.Adapt(Parse(
            """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":0}}"""));

        mapping.Supported.Should().BeTrue();
        mapping.Definition!.Nodes[1].Should().BeOfType<RasterFunctionIdentityNode>();
    }

    [UnitTest]
    public void Adapt_ImageServerDepthBoundary_AcceptsEightFunctionsAndRejectsNinth()
    {
        var maximum = ImageServerRasterFunctionDefinitionAdapter.Adapt(CreateIdentityChain(8));
        var tooDeep = ImageServerRasterFunctionDefinitionAdapter.Adapt(CreateIdentityChain(9));

        maximum.Supported.Should().BeTrue();
        maximum.Definition!.Nodes.Should().HaveCount(9);
        maximum.Definition.OutputNodeId.Should().Be("function-8");

        tooDeep.Supported.Should().BeFalse();
        tooDeep.Definition.Should().BeNull();
        tooDeep.Reason.Should().Contain("maximum depth of 8");
    }

    [UnitTest]
    public void Adapt_UnsupportedOrFreeFormInputs_FailClosed()
    {
        var cases = new[]
        {
            """{"rasterFunction":"Arithmetic","rasterFunctionArguments":{"Expression":"b1 + b2"}}""",
            """{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"BandIndexes":[0,1],"Expression":"b1 + b2"}}""",
            """{"rasterFunction":"Identity","rasterFunctionArguments":{"Raster2":{"rasterFunction":"Identity"}}}""",
            """{"rasterFunction":"Identity","rasterFunctionArguments":{"Raster":"$source"}}""",
            """{"rasterFunction":"Identity","outputPixelType":"U8"}""",
        };

        foreach (var json in cases)
        {
            var mapping = ImageServerRasterFunctionDefinitionAdapter.Adapt(Parse(json));

            mapping.Supported.Should().BeFalse();
            mapping.Definition.Should().BeNull();
            mapping.IsNotImplemented.Should().BeFalse();
            mapping.Reason.Should().NotBeNullOrWhiteSpace();
        }
    }

    [UnitTest]
    public void Adapt_RecognizedButUnsupportedOption_PreservesNotImplementedClassification()
    {
        var mapping = ImageServerRasterFunctionDefinitionAdapter.Adapt(Parse(
            """{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":1,"BandIndexes":[0,1]}}"""));

        mapping.Supported.Should().BeFalse();
        mapping.Definition.Should().BeNull();
        mapping.IsNotImplemented.Should().BeTrue();
        mapping.Reason.Should().Contain("Method 1");
    }

    [UnitTest]
    public void Adapt_AmbiguousAliases_FailClosed()
    {
        var cases = new[]
        {
            """{"rasterFunction":"ExtractBand","rasterFunctionArguments":{"BandIds":[0],"BandIDs":[1]}}""",
            """{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"BandIndexes":[0,1],"BandIds":[1,2]}}""",
            """{"rasterFunction":"Clip","rasterFunctionArguments":{"Extent":{"xmin":0,"ymin":0,"xmax":1,"ymax":1},"ClippingGeometry":{"xmin":1,"ymin":1,"xmax":2,"ymax":2}}}""",
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colormap":[[0,0,0,0]],"ColorrampName":"Red to Green"}}""",
            """{"rasterFunction":"Hillshade","rasterFunctionArguments":{"BandId":0,"BandIndex":1}}""",
        };

        foreach (var json in cases)
        {
            var mapping = ImageServerRasterFunctionDefinitionAdapter.Adapt(Parse(json));

            mapping.Supported.Should().BeFalse();
            mapping.Reason.Should().Contain("ambiguous");
        }
    }

    [UnitTest]
    public void Adapt_MalformedOptionalValues_FailClosedInsteadOfUsingDefaults()
    {
        var cases = new[]
        {
            """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":3,"NumberOfStandardDeviations":"many"}}""",
            """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5,"Statistics":[[10,"high"]]}}""",
            """{"rasterFunction":"Hillshade","rasterFunctionArguments":{"Azimuth":"west"}}""",
            """{"rasterFunction":"Clip","rasterFunctionArguments":{"ClippingType":"inside","Extent":{"xmin":0,"ymin":0,"xmax":1,"ymax":1}}}""",
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colormap":[[0,"red",0,0]]}}""",
        };

        foreach (var json in cases)
        {
            var mapping = ImageServerRasterFunctionDefinitionAdapter.Adapt(Parse(json));

            mapping.Supported.Should().BeFalse();
            mapping.Reason.Should().NotBeNullOrWhiteSpace();
        }
    }

    [UnitTest]
    public void Adapt_CanonicalValidatorRejectsInvalidTypedParameters()
    {
        var duplicateBands = ImageServerRasterFunctionDefinitionAdapter.Adapt(Parse(
            """{"rasterFunction":"ExtractBand","rasterFunctionArguments":{"BandIds":[0,0]}}"""));
        var sameIndexBands = ImageServerRasterFunctionDefinitionAdapter.Adapt(Parse(
            """{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":3,"BandIndexes":[0,0]}}"""));
        var unorderedColormap = ImageServerRasterFunctionDefinitionAdapter.Adapt(Parse(
            """{"rasterFunction":"Colormap","rasterFunctionArguments":{"Colormap":[[1,0,0,0],[0,255,255,255]]}}"""));

        duplicateBands.Supported.Should().BeFalse();
        sameIndexBands.Supported.Should().BeFalse();
        unorderedColormap.Supported.Should().BeFalse();
        duplicateBands.Reason.Should().Contain("canonical raster function");
    }
}
