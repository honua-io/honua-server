// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Migration.Domain;
using Honua.Db.Postgres.Features.Migration;

namespace Honua.Db.Postgres.Tests.Features.Import;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GeoServerAccessRuleTranslatorTests
{
    private static readonly GeoServerLayerInfo[] DemoLayers =
    [
        new GeoServerLayerInfo { Name = "observations", WorkspaceName = "demo" }
    ];

    [Fact]
    public void Translate_WithUnexpressibleWriteRule_DoesNotApplyBroaderGrant()
    {
        var rules = new[]
        {
            Rule("demo.observations.r", "ROLE_VIEWER"),
            Rule("demo.observations.w", "ROLE_EDITOR;cql=status=1"),
            Rule("demo.*.r", "*")
        };

        var translated = GeoServerAccessRuleTranslator.Translate(rules, DemoLayers);

        translated.LayerPoliciesBySourceId.Should().NotContainKey("layer:demo:observations");
        translated.UnexpressibleReviewItems.Should().HaveCount(2);
        translated.UnexpressibleReviewItems.Should().Contain(item => item.SourceId == "layer:demo:observations");
        translated.UnexpressibleReviewItems.Should().Contain(item => item.SourceId == "access-rule:demo.*.r");
    }

    [Fact]
    public void Translate_WithWildcardWriteRestrictionAndPerLayerReadOnly_FailsClosedIntoManualReview()
    {
        var rules = new[]
        {
            Rule("demo.observations.r", "ROLE_VIEWER"),
            Rule("demo.*.w", "ROLE_ADMIN")
        };

        var translated = GeoServerAccessRuleTranslator.Translate(rules, DemoLayers);

        translated.LayerPoliciesBySourceId.Should().NotContainKey("layer:demo:observations");
        translated.UnexpressibleReviewItems.Should().Contain(item => item.SourceId == "access-rule:demo.*.w");
        translated.UnexpressibleReviewItems.Should().Contain(item => item.SourceId == "layer:demo:observations");
    }

    [Fact]
    public void BuildWmsDimensionsExtension_WithoutTemporalColumn_EmitsDeclaredWmsTimeDimension()
    {
        var timeDimension = new GeoServerDimensionInfo
        {
            Enabled = true,
            Presentation = "CONTINUOUS_INTERVAL",
            Units = "ISO8601",
            DefaultValueStrategy = "MAXIMUM",
            NearestMatchEnabled = true,
            StartTime = DateTimeOffset.Parse("2025-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            EndTime = DateTimeOffset.Parse("2025-06-01T00:00:00Z", CultureInfo.InvariantCulture)
        };

        var extension = GeoServerImportService.BuildWmsDimensionsExtension(timeDimension, elevationDimension: null);

        extension.Should().NotBeNull();
        var dims = extension!.Value.GetProperty("dimensions");
        dims.GetArrayLength().Should().Be(1);
        dims[0].GetProperty("name").GetString().Should().Be("time");
        dims[0].GetProperty("units").GetString().Should().Be("ISO8601");
        dims[0].GetProperty("extent").GetString().Should().Be("2025-01-01T00:00:00Z/2025-06-01T00:00:00Z/0");
        dims[0].GetProperty("default").GetString().Should().Be("2025-06-01T00:00:00Z");
    }

    private static GeoServerLayerAccessRule Rule(string key, string rawValue)
    {
        var parts = key.Split('.');
        return new GeoServerLayerAccessRule
        {
            RuleKey = key,
            RawValue = rawValue,
            WorkspaceName = parts[0],
            LayerName = parts[1],
            Permission = parts[2],
            Roles = rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        };
    }
}
