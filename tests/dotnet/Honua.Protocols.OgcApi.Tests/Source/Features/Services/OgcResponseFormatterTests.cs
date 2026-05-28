// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features.Services;

/// <summary>
/// Unit tests for CSV formatting in OGC response formatter.
/// </summary>
public class OgcResponseFormatterTests
{
    [UnitTest]
    public void BuildCsvResponse_FormulaField_PrefixesWithSingleQuote()
    {
        // Arrange
        var features = new[]
        {
            CreateFeatureWithName("=SUM(A1:A2)")
        };

        // Act
        var csv = OgcResponseFormatter.BuildCsvResponse(features, ["name"]);

        // Assert
        var row = GetFirstDataRow(csv);
        Assert.True(row.StartsWith("1,'=SUM(A1:A2),", StringComparison.Ordinal), $"Unexpected CSV row: {row}");
    }

    [UnitTest]
    public void BuildCsvResponse_WhitespacePrefixedFormula_PrefixesWithSingleQuote()
    {
        // Arrange
        var features = new[]
        {
            CreateFeatureWithName("   =1+1")
        };

        // Act
        var csv = OgcResponseFormatter.BuildCsvResponse(features, ["name"]);

        // Assert
        var row = GetFirstDataRow(csv);
        Assert.True(row.StartsWith("1,'   =1+1,", StringComparison.Ordinal), $"Unexpected CSV row: {row}");
    }

    [UnitTest]
    public void BuildCsvResponse_NegativeNumericField_DoesNotPrefixSingleQuote()
    {
        // Arrange
        var features = new[]
        {
            CreateFeatureWithName("-42.5")
        };

        // Act
        var csv = OgcResponseFormatter.BuildCsvResponse(features, ["name"]);

        // Assert
        var row = GetFirstDataRow(csv);
        Assert.True(row.StartsWith("1,-42.5,", StringComparison.Ordinal), $"Unexpected CSV row: {row}");
        Assert.DoesNotContain("'-42.5", row, StringComparison.Ordinal);
    }

    [UnitTest]
    public void BuildCsvResponse_NegativeFormulaLikeField_PrefixesWithSingleQuote()
    {
        // Arrange
        var features = new[]
        {
            CreateFeatureWithName("-SUM(A1:A2)")
        };

        // Act
        var csv = OgcResponseFormatter.BuildCsvResponse(features, ["name"]);

        // Assert
        var row = GetFirstDataRow(csv);
        Assert.True(row.StartsWith("1,'-SUM(A1:A2),", StringComparison.Ordinal), $"Unexpected CSV row: {row}");
    }

    private static string GetFirstDataRow(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.True(lines.Length >= 2, $"Unexpected CSV payload: {csv}");
        Assert.Equal("id,name,geometry", lines[0]);
        return lines[1];
    }

    private static GeoJsonFeature CreateFeatureWithName(string value)
    {
        return new GeoJsonFeature
        {
            Id = 1,
            Properties = new Dictionary<string, object?>
            {
                ["name"] = value
            }
        };
    }
}
