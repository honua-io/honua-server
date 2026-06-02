// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Unit coverage for the shared Esri domain parser (honua-server#1255). The parser is
/// the single source of truth for coded-value/range capture and the 100-entry cap, so
/// the import inventory and the publish-path field builder agree on cap semantics.
/// </summary>
public sealed class EsriFieldDomainParserTests
{
    [Fact]
    public void Parse_CodedValueDomain_ProducesCanonicalCodedValues()
    {
        var field = ParseField("""
            {
              "name": "status",
              "domain": {
                "type": "codedValue",
                "name": "StatusDomain",
                "codedValues": [
                  { "name": "Active", "code": "A" },
                  { "name": "Closed", "code": "C" }
                ]
              }
            }
            """);

        var result = EsriFieldDomainParser.Parse(field);

        result.Truncated.Should().BeFalse();
        result.Domain.Should().NotBeNull();
        result.Domain!.Type.Should().Be("codedValue");
        result.Domain.Name.Should().Be("StatusDomain");
        result.Domain.CodedValues.Should().HaveCount(2);
        result.Domain.CodedValues.Select(v => v.Name).Should().BeEquivalentTo(["Active", "Closed"]);
    }

    [Fact]
    public void Parse_RangeDomain_ProducesTwoElementRange()
    {
        var field = ParseField("""
            {
              "name": "score",
              "domain": { "type": "range", "name": "ScoreRange", "range": [0, 100] }
            }
            """);

        var result = EsriFieldDomainParser.Parse(field);

        result.Truncated.Should().BeFalse();
        result.Domain.Should().NotBeNull();
        result.Domain!.Type.Should().Be("range");
        result.Domain.Name.Should().Be("ScoreRange");
        result.Domain.Range.Should().NotBeNull();
        result.Domain.Range!.Should().HaveCount(2);
        result.Domain.Range![0].GetInt32().Should().Be(0);
        result.Domain.Range![1].GetInt32().Should().Be(100);
    }

    [Fact]
    public void Parse_CodedValueDomainOverCap_ReportsTruncatedAndOmitsDomain()
    {
        var entries = new StringBuilder();
        for (var i = 0; i < EsriFieldDomainParser.CodedValueDomainCap + 1; i++)
        {
            if (i > 0)
            {
                entries.Append(',');
            }

            entries.Append(System.Globalization.CultureInfo.InvariantCulture, $"{{\"name\":\"Value{i}\",\"code\":{i}}}");
        }

        var field = ParseField($$"""
            {
              "name": "huge",
              "domain": { "type": "codedValue", "name": "HugeDomain", "codedValues": [{{entries}}] }
            }
            """);

        var result = EsriFieldDomainParser.Parse(field);

        // An over-cap domain is not persisted as a half-domain; it is reported truncated.
        result.Truncated.Should().BeTrue();
        result.Domain.Should().BeNull();
        result.DomainName.Should().Be("HugeDomain");
    }

    [Fact]
    public void Parse_FieldWithoutDomain_ReturnsNone()
    {
        var field = ParseField("""{ "name": "plain", "type": "esriFieldTypeString" }""");

        var result = EsriFieldDomainParser.Parse(field);

        result.Should().Be(EsriFieldDomainParseResult.None);
    }

    [Fact]
    public void Parse_DomainAtCapBoundary_PersistsFully()
    {
        var entries = new StringBuilder();
        for (var i = 0; i < EsriFieldDomainParser.CodedValueDomainCap; i++)
        {
            if (i > 0)
            {
                entries.Append(',');
            }

            entries.Append(System.Globalization.CultureInfo.InvariantCulture, $"{{\"name\":\"Value{i}\",\"code\":{i}}}");
        }

        var field = ParseField($$"""
            {
              "name": "atcap",
              "domain": { "type": "codedValue", "name": "AtCap", "codedValues": [{{entries}}] }
            }
            """);

        var result = EsriFieldDomainParser.Parse(field);

        result.Truncated.Should().BeFalse();
        result.Domain.Should().NotBeNull();
        result.Domain!.CodedValues.Should().HaveCount(EsriFieldDomainParser.CodedValueDomainCap);
    }

    private static JsonElement ParseField(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
