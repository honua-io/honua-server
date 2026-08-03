// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Geoprocessing.Raster.Functions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Tests.Features.Geoprocessing.Raster;

public sealed class RasterFunctionDefinitionTests
{
    [Fact]
    public void Validate_BoundedTypedChain_Succeeds()
    {
        var definition = CreateValidDefinition();

        var result = RasterFunctionValidator.Validate(definition);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_CycleAndDisconnectedNode_ReportsStableErrors()
    {
        var definition = new RasterFunctionDefinition
        {
            OutputNodeId = "first",
            Nodes =
            [
                new RasterFunctionIdentityNode { Id = "first", Inputs = ["second"] },
                new RasterFunctionIdentityNode { Id = "second", Inputs = ["first"] },
                new RasterFunctionInputNode { Id = "unused", InputName = "other" },
            ],
        };

        var result = RasterFunctionValidator.Validate(definition);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Code == RasterFunctionValidationCodes.CycleDetected);
        result.Errors.Should().Contain(error => error.Code == RasterFunctionValidationCodes.DisconnectedNode);
    }

    [Fact]
    public void Validate_ExcessiveDepthAndFanIn_FailsClosed()
    {
        var nodes = new List<RasterFunctionNode>
        {
            new RasterFunctionInputNode { Id = "source", InputName = "dem" },
        };
        for (var index = 1; index <= 9; index++)
        {
            nodes.Add(new RasterFunctionIdentityNode
            {
                Id = $"step{index}",
                Inputs = [index == 1 ? "source" : $"step{index - 1}"],
            });
        }

        nodes.Add(new RasterFunctionCompositeNode
        {
            Id = "wide",
            Method = RasterCompositeMethod.Last,
            Inputs = ["step9", "source", "step1", "step2", "step3", "step4", "step5", "step6", "step7"],
        });
        var definition = new RasterFunctionDefinition { Nodes = nodes, OutputNodeId = "wide" };

        var result = RasterFunctionValidator.Validate(definition);

        result.Errors.Should().Contain(error => error.Code == RasterFunctionValidationCodes.DepthExceeded);
        result.Errors.Should().Contain(error => error.Code == RasterFunctionValidationCodes.InvalidFanIn);
    }

    [Fact]
    public void Validate_SharedDependencyUsesLongestGraphDepth()
    {
        var definition = new RasterFunctionDefinition
        {
            OutputNodeId = "output",
            Nodes =
            [
                new RasterFunctionInputNode { Id = "source", InputName = "imagery" },
                new RasterFunctionIdentityNode { Id = "short", Inputs = ["source"] },
                new RasterFunctionIdentityNode { Id = "long1", Inputs = ["source"] },
                new RasterFunctionIdentityNode { Id = "long2", Inputs = ["long1"] },
                new RasterFunctionIdentityNode { Id = "long3", Inputs = ["long2"] },
                new RasterFunctionIdentityNode { Id = "long4", Inputs = ["long3"] },
                new RasterFunctionCompositeNode
                {
                    Id = "output",
                    Method = RasterCompositeMethod.Last,
                    Inputs = ["short", "long4"],
                },
            ],
        };

        var result = RasterFunctionValidator.Validate(
            definition,
            RasterFunctionValidationOptions.Default with { MaxDepth = 5 });

        result.Errors.Should().Contain(error => error.Code == RasterFunctionValidationCodes.DepthExceeded);
    }

    [Fact]
    public void Validate_ReclassificationOverlap_IsRejected()
    {
        var definition = new RasterFunctionDefinition
        {
            OutputNodeId = "classes",
            Nodes =
            [
                new RasterFunctionInputNode { Id = "source", InputName = "landcover" },
                new RasterFunctionReclassifyNode
                {
                    Id = "classes",
                    Inputs = ["source"],
                    OutputPixelType = RasterFunctionPixelType.UnsignedByte,
                    Rules =
                    [
                        new RasterReclassificationRule(0, 10, 1),
                        new RasterReclassificationRule(9, 20, 2),
                    ],
                },
            ],
        };

        var result = RasterFunctionValidator.Validate(definition);

        result.Errors.Should().ContainSingle(error =>
            error.Code == RasterFunctionValidationCodes.InvalidParameter
            && error.Path == "nodes[1]");
    }

    [Fact]
    public void Validate_ExplicitOutputCellBudget_IsEnforcedBeforePlanning()
    {
        var definition = new RasterFunctionDefinition
        {
            OutputNodeId = "resample",
            Nodes =
            [
                new RasterFunctionInputNode { Id = "source", InputName = "imagery" },
                new RasterFunctionResampleNode
                {
                    Id = "resample",
                    Inputs = ["source"],
                    Width = 20_000,
                    Height = 20_000,
                },
            ],
        };

        var result = RasterFunctionValidator.Validate(definition);

        result.Errors.Should().ContainSingle(error =>
            error.Code == RasterFunctionValidationCodes.InvalidParameter
            && error.Path == "nodes[1]");
    }

    [Fact]
    public void Validate_BandReferencesOutsideConfiguredRange_AreRejected()
    {
        var definition = new RasterFunctionDefinition
        {
            OutputNodeId = "index",
            Nodes =
            [
                new RasterFunctionInputNode { Id = "source", InputName = "imagery" },
                new RasterFunctionSpectralIndexNode
                {
                    Id = "index",
                    Inputs = ["source"],
                    Method = RasterSpectralIndexMethod.Ndvi,
                    PrimaryBand = 65,
                    SecondaryBand = 3,
                },
            ],
        };

        var result = RasterFunctionValidator.Validate(definition);

        result.Errors.Should().ContainSingle(error =>
            error.Code == RasterFunctionValidationCodes.InvalidParameter
            && error.Path == "nodes[1]");
    }

    [Fact]
    public void Json_RoundTripsPolymorphicNodes_WithoutReflectionMetadata()
    {
        var definition = CreateValidDefinition();

        var json = RasterFunctionJson.Serialize(definition);
        var roundTrip = RasterFunctionJson.Deserialize(json);

        roundTrip.Nodes.Should().HaveCount(definition.Nodes.Count);
        roundTrip.Nodes[0].Should().BeOfType<RasterFunctionInputNode>();
        roundTrip.Nodes[2].Should().BeOfType<RasterFunctionSpectralIndexNode>();
        RasterFunctionValidator.Validate(roundTrip).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Json_UnknownNodeType_FailsClosed()
    {
        const string json = """
            {
              "contractVersion": 1,
              "nodes": [
                { "nodeType": "arbitrary-code", "id": "unsafe", "inputs": [] }
              ],
              "outputNodeId": "unsafe"
            }
            """;

        var act = () => RasterFunctionJson.Deserialize(json);

        act.Should().Throw<System.Text.Json.JsonException>();
    }

    [Fact]
    public void ValidateInvocation_MissingTypedSourceBinding_FailsClosed()
    {
        var invocation = new RasterFunctionInvocation
        {
            Definition = CreateValidDefinition(),
            Sources = new Dictionary<string, RasterSourceDescriptor>(),
        };

        var result = RasterFunctionValidator.ValidateInvocation(invocation);

        result.Errors.Should().ContainSingle(error =>
            error.Code == RasterFunctionValidationCodes.InvalidSourceBinding
            && error.Path == "sources.imagery");
    }

    [Fact]
    public void ComputeSha256_NodeListOrderDoesNotChangeIdentity()
    {
        var first = CreateValidDefinition();
        var reordered = first with { Nodes = first.Nodes.Reverse().ToArray() };

        RasterFunctionJson.ComputeSha256(first).Should().Be(RasterFunctionJson.ComputeSha256(reordered));
    }

    [Fact]
    public void ComputeSha256_SemanticParameterChangeChangesIdentity()
    {
        var first = CreateValidDefinition();
        var changedNodes = first.Nodes.ToArray();
        changedNodes[2] = ((RasterFunctionSpectralIndexNode)changedNodes[2]) with
        {
            Method = RasterSpectralIndexMethod.Savi,
        };
        var changed = first with { Nodes = changedNodes };

        RasterFunctionJson.ComputeSha256(first).Should().NotBe(RasterFunctionJson.ComputeSha256(changed));
    }

    private static RasterFunctionDefinition CreateValidDefinition()
        => new()
        {
            OutputNodeId = "colors",
            Nodes =
            [
                new RasterFunctionInputNode { Id = "source", InputName = "imagery" },
                new RasterFunctionClipNode
                {
                    Id = "clip",
                    Inputs = ["source"],
                    Region = new RasterClipRegion
                    {
                        Geometry = [1, 2, 3, 4],
                        Srid = 4326,
                    },
                },
                new RasterFunctionSpectralIndexNode
                {
                    Id = "index",
                    Inputs = ["clip"],
                    Method = RasterSpectralIndexMethod.Ndvi,
                    PrimaryBand = 4,
                    SecondaryBand = 3,
                },
                new RasterFunctionStretchNode
                {
                    Id = "stretch",
                    Inputs = ["index"],
                    Stretch = new RasterStretch
                    {
                        StretchType = RasterStretchType.MinMax,
                        StatisticsMin = [-1],
                        StatisticsMax = [1],
                    },
                },
                new RasterFunctionColormapNode
                {
                    Id = "colors",
                    Inputs = ["stretch"],
                    Colormap = new RasterColormap
                    {
                        Entries =
                        [
                            new RasterColormapEntry(0, 0, 0, 0, 255),
                            new RasterColormapEntry(255, 0, 255, 0, 255),
                        ],
                    },
                },
            ],
        };
}
