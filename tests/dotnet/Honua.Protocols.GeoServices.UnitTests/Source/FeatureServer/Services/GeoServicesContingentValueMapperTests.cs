// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Unit tests for the contingent-value mapper (#1878 Phase 1): maps the canonical Metadata v2
/// contingent value groups onto the Esri queryContingentValues per-layer definition shape.
/// </summary>
public sealed class GeoServicesContingentValueMapperTests
{
    private static JsonElement Number(int value)
        => JsonSerializer.SerializeToElement(value);

    [UnitTest]
    public void Map_NoGroups_ReturnsNull()
    {
        var result = GeoServicesContingentValueMapper.Map(0, Array.Empty<MetadataV2ContingentValueGroup>());

        result.Should().BeNull();
    }

    [UnitTest]
    public void Map_GroupWithCodeAndRange_ProjectsDefinition()
    {
        var group = new MetadataV2ContingentValueGroup
        {
            Name = "material-diameter",
            Restrictive = true,
            Fields = ["material", "diameter"],
            ContingentValues =
            [
                new MetadataV2ContingentValue
                {
                    Id = 2,
                    SubtypeCode = Number(1),
                    Values = new Dictionary<string, MetadataV2ContingentFieldValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["material"] = new() { Type = "code", Code = Number(10) },
                        ["diameter"] = new()
                        {
                            Type = "range",
                            Range = [Number(0), Number(12)],
                        },
                    },
                },
                new MetadataV2ContingentValue
                {
                    Id = 1,
                    Values = new Dictionary<string, MetadataV2ContingentFieldValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["material"] = new() { Type = "any" },
                    },
                },
            ],
        };

        var result = GeoServicesContingentValueMapper.Map(7, [group]);

        result.Should().NotBeNull();
        result!.Id.Should().Be(7);
        result.FieldGroups.Should().HaveCount(1);

        var fieldGroup = result.FieldGroups[0];
        fieldGroup.Name.Should().Be("material-diameter");
        fieldGroup.Restrictive.Should().BeTrue();
        fieldGroup.Fields.Should().Equal("material", "diameter");

        // Rows are ordered deterministically by id.
        fieldGroup.ContingentValues.Should().HaveCount(2);
        fieldGroup.ContingentValues[0].Id.Should().Be(1);
        fieldGroup.ContingentValues[1].Id.Should().Be(2);

        var codeRow = fieldGroup.ContingentValues[1];
        codeRow.SubtypeCode.Should().NotBeNull();
        codeRow.SubtypeCode!.Value.GetInt32().Should().Be(1);
        codeRow.Values["material"].Type.Should().Be("code");
        codeRow.Values["material"].Code!.Value.GetInt32().Should().Be(10);
        codeRow.Values["diameter"].Type.Should().Be("range");
        codeRow.Values["diameter"].Range.Should().NotBeNull();
        codeRow.Values["diameter"].Range!.Should().HaveCount(2);
        codeRow.Values["diameter"].Range![1].GetInt32().Should().Be(12);

        var anyRow = fieldGroup.ContingentValues[0];
        anyRow.SubtypeCode.Should().BeNull();
        anyRow.Values["material"].Type.Should().Be("any");
    }
}
